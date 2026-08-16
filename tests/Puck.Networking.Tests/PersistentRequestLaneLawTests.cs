using System.Net;
using System.Net.Sockets;

using Xunit;

namespace Puck.Networking.Tests;

file enum FakeRequestKind : byte {
    Ping = 1,
}
file enum FakeResponseKind : byte {
    Pong = 1,
}
/// <summary>A minimal <see cref="ILaneProtocol{TRequestKind,TResponseKind}"/> riding the real
/// <see cref="HandshakeWireFormat"/>/<see cref="WireFrame"/> primitives, so these laws exercise the same wire
/// grammar a production dialect would.</summary>
file sealed class FakeLaneProtocol : ILaneProtocol<FakeRequestKind, FakeResponseKind> {
    public Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct) => Task.CompletedTask;
    public async Task<LaneResponse<FakeResponseKind>> ReadResponseAsync(Stream stream, CancellationToken ct) {
        var read = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: 4096,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (read.Ok
            ? new LaneResponse<FakeResponseKind>(
                Kind: ((FakeResponseKind)read.Kind),
                Body: read.Body,
                Failure: default
            )
            : LaneResponse<FakeResponseKind>.Refused(
                refusal: read.Failure.Refusal,
                detail: read.Failure.Detail
            )
        );
    }
    public Task WriteHelloAsync(Stream stream, CancellationToken ct) => HandshakeWireFormat.WriteHelloAsync(
        ct: ct,
        key: 0xF00D,
        stream: stream
    );
    public Task WriteRequestAsync(Stream stream, FakeRequestKind kind, ReadOnlyMemory<byte> body, CancellationToken ct) => WireFrame.WriteAsync(
        body: body,
        ct: ct,
        kind: ((byte)kind),
        stream: stream
    );
}

/// <summary>
/// Laws for <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> — the state machine
/// <c>WorldRemoteAuthority</c>'s federation lanes now ride. Every scenario here drives the real class over a real
/// loopback socket; nothing pokes its private fields.
/// </summary>
public sealed class PersistentRequestLaneLawTests {
    private static IPEndPoint UnreachableEndpoint() {
        using var probe = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        probe.Start();

        var endpoint = ((IPEndPoint)probe.LocalEndpoint);

        probe.Stop();

        return endpoint;
    }

    /// <summary>A break on an already-established connection reconnects and re-sends exactly once, without ever
    /// calling the lane unreachable. Falsifier: dropping the <c>hadConnection</c> branch (always taking the
    /// "report immediately" arm) turns the second request's answer into a refusal instead of a successful retry.</summary>
    [Fact]
    public async Task BreakOnEstablishedConnection_ReconnectsAndResendsOnce_WithoutEnteringBackoff() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);

        using var deadline = Laws.SocketDeadline();
        var serverTask = Task.Run(
            function: async () => {
                // First connection: answers request #1 normally, then closes without answering request #2 (the break).
                using (var first = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token)) {
                    var stream = first.GetStream();

                    await HandshakeWireFormat.TryReadExactAsync(
                        buffer: new byte[HandshakeWireFormat.HelloBytes],
                        ct: deadline.Token,
                        stream: stream
                    );

                    var request = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );

                    await WireFrame.WriteAsync(
                        body: request.Body,
                        ct: deadline.Token,
                        kind: ((byte)FakeResponseKind.Pong),
                        stream: stream
                    );

                    // The break: read and discard request #2, then close without a reply.
                    _ = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );
                }

                // Second connection: the resend of request #2 lands here and gets a real answer.
                using var second = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token);
                var secondStream = second.GetStream();

                await HandshakeWireFormat.TryReadExactAsync(
                    buffer: new byte[HandshakeWireFormat.HelloBytes],
                    ct: deadline.Token,
                    stream: secondStream
                );

                var resend = await WireFrame.ReadAsync(
                    stream: secondStream,
                    maxFrameBytes: 4096,
                    ct: deadline.Token
                );

                await WireFrame.WriteAsync(
                    body: resend.Body,
                    ct: deadline.Token,
                    kind: ((byte)FakeResponseKind.Pong),
                    stream: secondStream
                );
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var first = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        Assert.True(
            condition: first.Ok,
            userMessage: first.Failure.ToString()
        );

        var second = await lane.Enqueue(
            body: [2],
            kind: FakeRequestKind.Ping
        );

        await serverTask;

        Assert.True(
            condition: second.Ok,
            userMessage: second.Failure.ToString()
        );
        Assert.Equal(
            expected: ((byte)2),
            actual: Assert.Single(collection: second.Body)
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a break on a live connection must not enter backoff"
        );
    }
    /// <summary>A connect failure is retried once, then declares the lane unreachable — never a third attempt.
    /// Falsifier: changing the lifted <c>++connectFailures &gt;= 2</c> threshold to 3 makes the endpoint delegate
    /// called a third time, turning this red.</summary>
    [Fact]
    public async Task ConnectFailure_DeclaresUnreachableAfterTwoAttempts_NeverThree() {
        var unreachable = UnreachableEndpoint();
        var attempts = 0;

        using var deadline = Laws.SocketDeadline();
        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => {
                Interlocked.Increment(location: ref attempts);

                return new LaneRoute(
                    Endpoint: unreachable,
                    Description: unreachable.ToString()
                );
            },
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var response = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Equal(
            expected: 2,
            actual: Volatile.Read(location: ref attempts)
        );
    }
    /// <summary>A route republished between two connect attempts is picked up on the next attempt, but each
    /// individual attempt connects to and records exactly one route generation — never an endpoint sampled from one
    /// generation paired with a description sampled from another. The second listener never accepts a connection:
    /// if a connect attempt read the route twice, the second read (returning the second generation) would leak into
    /// either the socket dialed or the description recorded, and this would either connect to the wrong listener or
    /// desynchronize the two.</summary>
    [Fact]
    public async Task Connect_NeverMixesOneRouteGenerationsEndpointWithAnothersDescription() {
        using var listenerA = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );
        using var listenerB = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listenerA.Start();
        listenerB.Start();

        var endpointA = ((IPEndPoint)listenerA.LocalEndpoint);
        var endpointB = ((IPEndPoint)listenerB.LocalEndpoint);
        var routeReads = 0;
        var connectedToB = false;

        using var deadline = Laws.SocketDeadline();
        var serverTask = Task.Run(
            function: async () => {
                using var client = await listenerA.AcceptTcpClientAsync(cancellationToken: deadline.Token);
                var stream = client.GetStream();

                await HandshakeWireFormat.TryReadExactAsync(
                    buffer: new byte[HandshakeWireFormat.HelloBytes],
                    ct: deadline.Token,
                    stream: stream
                );

                var request = await WireFrame.ReadAsync(
                    stream: stream,
                    maxFrameBytes: 4096,
                    ct: deadline.Token
                );

                await WireFrame.WriteAsync(
                    body: request.Body,
                    ct: deadline.Token,
                    kind: ((byte)FakeResponseKind.Pong),
                    stream: stream
                );
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        var watchBTask = Task.Run(
            function: async () => {
                try {
                    using var client = await listenerB.AcceptTcpClientAsync(cancellationToken: deadline.Token);

                    connectedToB = true;
                } catch (Exception exception) when ((exception is OperationCanceledException or SocketException)) {
                    // Nothing ever dialed B before the listener was stopped — the passing outcome.
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => {
                var reads = Interlocked.Increment(location: ref routeReads);

                // Every read after the very first — including a second read inside the SAME connect attempt were
                // one ever taken — returns the other generation, so a leaked second read is observable.
                return ((reads == 1)
                    ? new LaneRoute(
                        Endpoint: endpointA,
                        Description: endpointA.ToString()
                    )
                    : new LaneRoute(
                        Endpoint: endpointB,
                        Description: endpointB.ToString()
                    )
                );
            },
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var response = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        await serverTask;
        listenerB.Stop();
        await watchBTask;

        Assert.True(
            condition: response.Ok,
            userMessage: response.Failure.ToString()
        );
        Assert.False(
            condition: connectedToB,
            userMessage: "a single connect attempt must never dial the second route generation"
        );
    }
    /// <summary>A connect attempt samples its route exactly once — the same snapshot serves the reconnect-needed
    /// check, the socket it dials, and the description recorded for that socket. Falsifier: splitting the read into
    /// two calls (one to decide the endpoint, a later one to record its description, as a route republished between
    /// them would then let a socket connected to one endpoint get recorded under a different endpoint's
    /// description) pushes the count above one and turns this red.</summary>
    [Fact]
    public async Task Connect_SamplesTheRouteExactlyOnce_PerAttempt() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        var routeReads = 0;

        using var deadline = Laws.SocketDeadline();
        var serverTask = Task.Run(
            function: async () => {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token);
                var stream = client.GetStream();

                await HandshakeWireFormat.TryReadExactAsync(
                    buffer: new byte[HandshakeWireFormat.HelloBytes],
                    ct: deadline.Token,
                    stream: stream
                );

                var request = await WireFrame.ReadAsync(
                    stream: stream,
                    maxFrameBytes: 4096,
                    ct: deadline.Token
                );

                await WireFrame.WriteAsync(
                    body: request.Body,
                    ct: deadline.Token,
                    kind: ((byte)FakeResponseKind.Pong),
                    stream: stream
                );
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => {
                Interlocked.Increment(location: ref routeReads);

                return new LaneRoute(
                    Endpoint: endpoint,
                    Description: endpoint.ToString()
                );
            },
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var response = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        await serverTask;

        Assert.True(
            condition: response.Ok,
            userMessage: response.Failure.ToString()
        );
        Assert.Equal(
            expected: 1,
            actual: Volatile.Read(location: ref routeReads)
        );
    }
    /// <summary>Requests enqueued in sequence are served — and answered — in that same order, one fully completed
    /// round trip at a time, over the one shared connection. Each response echoes its own request's payload, so a
    /// worker that ever let two requests share the stream concurrently would corrupt or misroute an answer.</summary>
    [Fact]
    public async Task SequentialEnqueue_ServesStrictFifoOrder_WithNoCrossTalk() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        const int requestCount = 16;
        var observedOrder = new List<int>();

        using var deadline = Laws.SocketDeadline();
        var serverTask = Task.Run(
            function: async () => {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token);
                var stream = client.GetStream();

                await HandshakeWireFormat.TryReadExactAsync(
                    buffer: new byte[HandshakeWireFormat.HelloBytes],
                    ct: deadline.Token,
                    stream: stream
                );

                for (var index = 0; (index < requestCount); index++) {
                    var request = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );

                    observedOrder.Add(item: request.Body[0]);
                    await WireFrame.WriteAsync(
                        body: request.Body,
                        ct: deadline.Token,
                        kind: ((byte)FakeResponseKind.Pong),
                        stream: stream
                    );
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var pending = new Task<LaneResponse<FakeResponseKind>>[requestCount];

        for (var index = 0; (index < requestCount); index++) {
            pending[index] = lane.Enqueue(
                body: [((byte)index)],
                kind: FakeRequestKind.Ping
            );
        }

        var results = await Task.WhenAll(pending);

        await serverTask;

        for (var index = 0; (index < requestCount); index++) {
            Assert.True(
                condition: results[index].Ok,
                userMessage: results[index].Failure.ToString()
            );
            Assert.Equal(
                expected: ((byte)index),
                actual: Assert.Single(collection: results[index].Body)
            );
        }

        Assert.Equal(
            expected: Enumerable.Range(
                count: requestCount,
                start: 0
            ),
            actual: observedOrder
        );
    }
    /// <summary>A throwing <c>onUnavailable</c> callback is contained: the request whose connect failure raised it
    /// still gets its named refusal, and the worker survives to serve a later <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Enqueue"/>.
    /// Falsifier: letting the callback's exception escape <c>Unreachable</c> and back into <c>RunAsync</c>'s
    /// unguarded <c>await ServeAsync(...)</c> faults the worker task before it ever completes the current request's
    /// <see cref="TaskCompletionSource{TResult}"/>, so the first <c>await</c> below hangs until the test's own
    /// deadline, and this turns red.</summary>
    [Fact]
    public async Task ThrowingOnUnavailableCallback_FailsCurrentRequest_AndKeepsWorkerAliveForLaterEnqueues() {
        var unreachable = UnreachableEndpoint();

        using var deadline = Laws.SocketDeadline();
        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            onUnavailable: _ => throw new InvalidOperationException(message: "the callback itself is broken"),
            protocol: new FakeLaneProtocol(),
            route: () => new LaneRoute(
                Endpoint: unreachable,
                Description: unreachable.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var first = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            timeout: TimeSpan.FromSeconds(value: 10),
            cancellationToken: deadline.Token
        );

        Assert.False(condition: first.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: first.Failure.Refusal
        );

        var second = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            timeout: TimeSpan.FromSeconds(value: 10),
            cancellationToken: deadline.Token
        );

        Assert.False(condition: second.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: second.Failure.Refusal
        );
    }
    /// <summary>Once unreachable, the lane reports <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.IsAvailable"/>
    /// as <see langword="false"/> for exactly its configured backoff window, then recovers.</summary>
    [Fact]
    public async Task Unreachable_StaysUnavailableForTheBackoffWindow_ThenRecovers() {
        var unreachable = UnreachableEndpoint();
        var backoff = TimeSpan.FromMilliseconds(value: 200);

        using var deadline = Laws.SocketDeadline();
        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            route: () => new LaneRoute(
                Endpoint: unreachable,
                Description: unreachable.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: backoff
        );

        _ = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        Assert.False(condition: lane.IsAvailable);

        await Task.Delay(
            delay: (backoff + TimeSpan.FromMilliseconds(value: 100)),
            cancellationToken: deadline.Token
        );

        Assert.True(condition: lane.IsAvailable);
    }
}
