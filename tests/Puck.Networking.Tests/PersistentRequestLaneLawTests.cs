using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Xunit;

namespace Puck.Networking.Tests;

file enum FakeRequestKind : byte {
    Ping = 1,
    /// <summary>The one kind the fake protocol refuses to re-send — the peer would apply it twice.</summary>
    Submission = 2,
}
file enum FakeResponseKind : byte {
    Pong = 1,
}
/// <summary>A minimal <see cref="ILaneProtocol{TRequestKind,TResponseKind}"/> riding the real
/// <see cref="HandshakeWireFormat"/>/<see cref="WireFrame"/> primitives, so these laws exercise the same wire
/// grammar a production dialect would.</summary>
file sealed class FakeLaneProtocol : ILaneProtocol<FakeRequestKind, FakeResponseKind> {
    /// <summary>Gets or sets how many further <see cref="ReadResponseAsync"/> calls throw an
    /// <see cref="InvalidOperationException"/> before reading a byte — an exception outside the wire vocabulary, the
    /// shape a dialect's own bug takes.</summary>
    public int ReadResponseFaultsRemaining { get; set; }
    /// <summary>Gets a value indicating whether <see cref="AuthenticateAsync"/> parks until its token is cancelled —
    /// a peer that accepts the connection and the Hello and then never completes the exchange.</summary>
    public bool StallsAuthentication { get; init; }
    /// <summary>Gets a value indicating whether <see cref="WriteRequestAsync"/> parks until its token is cancelled
    /// without writing a byte — a request write that never completes, the shape a peer whose receive window is full
    /// gives the dialect.</summary>
    public bool StallsRequestWrite { get; init; }

    public Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct) => (StallsAuthentication
        ? Task.Delay(
            cancellationToken: ct,
            millisecondsDelay: Timeout.Infinite
        )
        : Task.CompletedTask
    );
    public bool MayResend(FakeRequestKind kind) => (kind switch {
        FakeRequestKind.Submission => false,
        _ => true,
    });
    public async Task<LaneResponse<FakeResponseKind>> ReadResponseAsync(Stream stream, CancellationToken ct) {
        if (ReadResponseFaultsRemaining > 0) {
            ReadResponseFaultsRemaining--;

            throw new InvalidOperationException(message: "the dialect itself is broken");
        }

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
    public Task WriteRequestAsync(Stream stream, FakeRequestKind kind, ReadOnlyMemory<byte> body, CancellationToken ct) => (StallsRequestWrite
        ? Task.Delay(
            cancellationToken: ct,
            millisecondsDelay: Timeout.Infinite
        )
        : WireFrame.WriteAsync(
            body: body,
            ct: ct,
            kind: ((byte)kind),
            stream: stream
        )
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
            actual: Assert.Single(collection: second.Body.ToArray())
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a break on a live connection must not enter backoff"
        );
    }
    /// <summary>A break on an established connection while a kind the protocol refuses to re-send is in flight is
    /// answered <see cref="WireRefusal.ConnectionClosed"/> with the request left in doubt: the peer received it exactly
    /// once, no second connection is dialed, and the lane never enters backoff. Falsifier: skipping the
    /// <see cref="ILaneProtocol{TRequestKind,TResponseKind}.MayResend"/> check on the break arm re-sends the request
    /// over a fresh connection, which the listener accepts and this law counts.</summary>
    [Fact]
    public async Task BreakOnEstablishedConnection_WhenTheKindMayNotBeResent_AnswersConnectionClosed_AndSendsExactlyOnce() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        var acceptedAgain = false;

        using var deadline = Laws.SocketDeadline();
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
        var serverTask = Task.Run(
            function: async () => {
                // First connection: answers the ping normally, then closes without answering the submission (the break).
                using (var first = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token)) {
                    var stream = first.GetStream();

                    await HandshakeWireFormat.TryReadExactAsync(
                        buffer: new byte[HandshakeWireFormat.HelloBytes],
                        ct: deadline.Token,
                        stream: stream
                    );

                    var ping = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );

                    await WireFrame.WriteAsync(
                        body: ping.Body,
                        ct: deadline.Token,
                        kind: ((byte)FakeResponseKind.Pong),
                        stream: stream
                    );

                    // The break: read and discard the submission, then close without a reply.
                    _ = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );
                }

                try {
                    using var second = await listener.AcceptTcpClientAsync(cancellationToken: watch.Token);

                    acceptedAgain = true;
                } catch (OperationCanceledException) {
                    // Nothing dialed again before the answer arrived — the passing outcome.
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
            kind: FakeRequestKind.Submission
        );

        watch.Cancel();
        await serverTask;

        Assert.False(condition: second.Ok);
        Assert.Equal(
            expected: WireRefusal.ConnectionClosed,
            actual: second.Failure.Refusal
        );
        Assert.Contains(
            actualString: second.Failure.Detail,
            expectedSubstring: "may or may not have been applied"
        );
        Assert.False(
            condition: acceptedAgain,
            userMessage: "a kind the protocol refuses to re-send must never be dialed a second time"
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "an in-doubt request must not enter backoff"
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
    /// <summary>A <c>connectRetryDelay</c> or <c>requestTimeout</c> outside [0, 1 day] is refused by the constructor,
    /// naming the parameter, before any worker starts; the bounds themselves are admitted. Falsifier: storing the
    /// values unchecked lets a negative <c>requestTimeout</c> reach the per-attempt <c>CancelAfter</c>, which answers
    /// every request <see cref="WireRefusal.LaneUnavailable"/> naming <see cref="ArgumentOutOfRangeException"/>
    /// without ever touching a socket, and then makes <c>Dispose</c> throw from its bounded join — the one exception
    /// its catch does not cover.</summary>
    [Fact]
    public void Constructor_RefusesATimingOutsideItsRange_ByName() {
        var unreachable = UnreachableEndpoint();

        using var deadline = Laws.SocketDeadline();

        PersistentRequestLane<FakeRequestKind, FakeResponseKind> Build(TimeSpan connectRetryDelay, TimeSpan requestTimeout) => new(
            connectRetryDelay: connectRetryDelay,
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: requestTimeout,
            route: () => new LaneRoute(
                Endpoint: unreachable,
                Description: unreachable.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var negativeTimeout = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            requestTimeout: TimeSpan.FromSeconds(value: -5)
        ));
        var timeoutOverADay = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            requestTimeout: (TimeSpan.FromDays(value: 1) + TimeSpan.FromTicks(value: 1))
        ));
        var negativeDelay = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            connectRetryDelay: TimeSpan.FromTicks(value: -1),
            requestTimeout: TimeSpan.FromSeconds(value: 10)
        ));
        var delayOverADay = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
            connectRetryDelay: (TimeSpan.FromDays(value: 1) + TimeSpan.FromTicks(value: 1)),
            requestTimeout: TimeSpan.FromSeconds(value: 10)
        ));

        Assert.Equal(
            expected: "requestTimeout",
            actual: negativeTimeout.ParamName
        );
        Assert.Equal(
            expected: "requestTimeout",
            actual: timeoutOverADay.ParamName
        );
        Assert.Equal(
            expected: "connectRetryDelay",
            actual: negativeDelay.ParamName
        );
        Assert.Equal(
            expected: "connectRetryDelay",
            actual: delayOverADay.ParamName
        );

        // The control: both ends of the admitted range construct, and each lane disposes without throwing.
        using (Build(
            connectRetryDelay: TimeSpan.Zero,
            requestTimeout: TimeSpan.Zero
        )) {
        }

        using (Build(
            connectRetryDelay: TimeSpan.FromDays(value: 1),
            requestTimeout: TimeSpan.FromDays(value: 1)
        )) {
        }
    }
    /// <summary>An <c>onUnavailable</c> callback that disposes the lane returns promptly — it runs on the thread pool,
    /// never on the worker that <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Dispose"/> joins — and
    /// the request that raised it still gets its named refusal. Falsifier: invoking the callback on the worker makes
    /// <c>Dispose</c> join the worker from the worker, parking the callback for the whole bounded join
    /// (<c>requestTimeout</c> plus one second, eleven seconds here) before it can return — past the five-second bound
    /// below — and stranding the request's answer behind it.</summary>
    [Fact]
    public async Task Dispose_FromInsideOnUnavailable_Returns() {
        var unreachable = UnreachableEndpoint();
        var callbackReturned = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        PersistentRequestLane<FakeRequestKind, FakeResponseKind>? lane = null;

        using var deadline = Laws.SocketDeadline();

        lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            onUnavailable: exception => {
                lane!.Dispose();
                _ = callbackReturned.TrySetResult();
            },
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => new LaneRoute(
                Endpoint: unreachable,
                Description: unreachable.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        try {
            var response = await lane.Enqueue(
                body: [],
                kind: FakeRequestKind.Ping
            ).WaitAsync(
                cancellationToken: deadline.Token,
                timeout: TimeSpan.FromSeconds(value: 10)
            );

            await callbackReturned.Task.WaitAsync(
                cancellationToken: deadline.Token,
                timeout: TimeSpan.FromSeconds(value: 5)
            );

            Assert.False(condition: response.Ok);
            Assert.Equal(
                expected: WireRefusal.LaneUnavailable,
                actual: response.Failure.Refusal
            );
        } finally {
            lane.Dispose();
        }
    }
    /// <summary>A second <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Dispose"/> is a no-op.
    /// Falsifier: dropping the interlocked guard makes the second call cancel an already-disposed
    /// <see cref="CancellationTokenSource"/>, which throws <see cref="ObjectDisposedException"/>.</summary>
    [Fact]
    public void Dispose_Twice_IsIdempotent_AndNeverThrows() {
        var unreachable = UnreachableEndpoint();

        using var deadline = Laws.SocketDeadline();
        var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => new LaneRoute(
                Endpoint: unreachable,
                Description: unreachable.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        lane.Dispose();
        lane.Dispose();
    }
    /// <summary>A request queued after <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Dispose"/> is
    /// answered <see cref="WireRefusal.LaneUnavailable"/> synchronously — the task is already complete when
    /// <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Enqueue"/> returns — with no socket attempt, even
    /// on a lane that served traffic before it was disposed. Falsifier: leaving the queue's writer open across
    /// <c>Dispose</c> accepts the write, and with no worker left to drain it the task never completes.</summary>
    [Fact]
    public async Task EnqueueAfterDispose_AnswersLaneUnavailable_AtOnce() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);

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

        var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var served = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        await serverTask;

        Assert.True(
            condition: served.Ok,
            userMessage: served.Failure.ToString()
        );

        lane.Dispose();

        var late = lane.Enqueue(
            body: [2],
            kind: FakeRequestKind.Ping
        );

        Assert.True(
            condition: late.IsCompleted,
            userMessage: "a request queued after Dispose must be answered before Enqueue returns"
        );

        var response = await late;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "is closed"
        );
    }
    /// <summary>A request queued after the lifetime token cancelled — with no <c>Dispose</c> in between, the shape a
    /// host's shutdown token takes — is answered <see cref="WireRefusal.LaneUnavailable"/> rather than stranded: the
    /// worker's own exit closes the queue, so nothing can be written into a channel nobody reads. Falsifier: closing
    /// the queue only in <c>Dispose</c> lets the write succeed, and with the worker gone the task below never
    /// completes inside its bound.</summary>
    [Fact]
    public async Task EnqueueAfterLifetimeCancelled_AnswersLaneUnavailable_WithoutDispose() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);

        using var deadline = Laws.SocketDeadline();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
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
            lifetime: lifetime.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var served = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        await serverTask;

        Assert.True(
            condition: served.Ok,
            userMessage: served.Failure.ToString()
        );

        lifetime.Cancel();

        // The worker leaves on the cancel; the pause lets it finish so the request below meets a closed queue rather
        // than the exit drain — either answers it, but only the closed queue proves the door shut on the worker's exit.
        await Task.Delay(
            cancellationToken: deadline.Token,
            delay: TimeSpan.FromMilliseconds(value: 200)
        );

        var response = await lane.Enqueue(
            body: [2],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 5)
        );

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "closed"
        );
    }
    /// <summary>Cancelling the lifetime token with no <c>Dispose</c> in between releases the connected socket: the
    /// peer parked on its next read observes the close (<see cref="WireRefusal.ConnectionClosed"/> at the prefix)
    /// promptly, rather than holding a serving task on a connection that will never carry another frame until this
    /// process's finalizer runs. Falsifier: dropping the socket only in <c>Dispose</c> leaves the peer's read pending
    /// past the five-second bound below.</summary>
    [Fact]
    public async Task LifetimeCancelled_ReleasesTheSocket_WithoutDispose() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);

        using var deadline = Laws.SocketDeadline();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
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

                // The lane's next move is to leave; a peer parked on the next frame sees that as a prefix EOF.
                return await WireFrame.ReadAsync(
                    stream: stream,
                    maxFrameBytes: 4096,
                    ct: deadline.Token
                );
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: lifetime.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var served = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        Assert.True(
            condition: served.Ok,
            userMessage: served.Failure.ToString()
        );

        lifetime.Cancel();

        var afterClose = await serverTask.WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 5)
        );

        Assert.False(condition: afterClose.Ok);
        Assert.Equal(
            expected: WireRefusal.ConnectionClosed,
            actual: afterClose.Failure.Refusal
        );
    }
    /// <summary>A protocol that throws outside the wire vocabulary mid-exchange costs only that request: it is
    /// answered <see cref="WireRefusal.LaneUnavailable"/> naming the exception, the connection it happened on is
    /// dropped, the lane does not enter backoff, and the worker survives to serve the next request over a fresh
    /// connection. Falsifier: removing the catch-all around <c>ServeAsync</c> faults the worker on the first request,
    /// whose completion is then never set, so the first <c>await</c> below hangs until its own bound.</summary>
    [Fact]
    public async Task ProtocolExceptionOutsideTheWireVocabulary_AnswersLaneUnavailable_AndKeepsWorkerAliveForTheNextRequest() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);

        using var deadline = Laws.SocketDeadline();
        var serverTask = Task.Run(
            function: async () => {
                // First connection: the request arrives, but the dialect throws before it reads any reply, so none is
                // written; the lane drops this socket.
                using (var first = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token)) {
                    var stream = first.GetStream();

                    await HandshakeWireFormat.TryReadExactAsync(
                        buffer: new byte[HandshakeWireFormat.HelloBytes],
                        ct: deadline.Token,
                        stream: stream
                    );
                    _ = await WireFrame.ReadAsync(
                        stream: stream,
                        maxFrameBytes: 4096,
                        ct: deadline.Token
                    );
                }

                // Second connection: the next request lands on a fresh socket and gets a real answer.
                using var second = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token);
                var secondStream = second.GetStream();

                await HandshakeWireFormat.TryReadExactAsync(
                    buffer: new byte[HandshakeWireFormat.HelloBytes],
                    ct: deadline.Token,
                    stream: secondStream
                );

                var request = await WireFrame.ReadAsync(
                    stream: secondStream,
                    maxFrameBytes: 4096,
                    ct: deadline.Token
                );

                await WireFrame.WriteAsync(
                    body: request.Body,
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
            protocol: new FakeLaneProtocol { ReadResponseFaultsRemaining = 1 },
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 10)
        );

        Assert.False(condition: first.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: first.Failure.Refusal
        );
        Assert.Contains(
            actualString: first.Failure.Detail,
            expectedSubstring: nameof(InvalidOperationException)
        );

        var second = await lane.Enqueue(
            body: [2],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 10)
        );

        await serverTask;

        Assert.True(
            condition: second.Ok,
            userMessage: second.Failure.ToString()
        );
        Assert.Equal(
            expected: ((byte)2),
            actual: Assert.Single(collection: second.Body.ToArray())
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a dialect's own exception is the request's answer, never a backoff"
        );
    }
    /// <summary>A request that succeeds while the lane is inside its unreachable backoff clears that backoff at once:
    /// <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.IsAvailable"/> reports <see langword="true"/> again
    /// long before the window would have expired on its own. Falsifier: resetting only the noted flag (not the
    /// backoff deadline) on success leaves it <see langword="false"/> for the full thirty-second window this law never
    /// waits out.</summary>
    [Fact]
    public async Task QueuedSuccess_ResetsAvailability_InsideTheBackoffWindow() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var unreachable = UnreachableEndpoint();
        var reachable = ((IPEndPoint)listener.LocalEndpoint);
        var peerIsUp = false;

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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
            route: () => {
                // The route is republished from the absent peer to the live one between the two requests; the
                // worker reads it after the queue handoff, which orders the write below before this read.
                var endpoint = (Volatile.Read(location: ref peerIsUp)
                    ? reachable
                    : unreachable
                );

                return new LaneRoute(
                    Endpoint: endpoint,
                    Description: endpoint.ToString()
                );
            },
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var refused = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: refused.Failure.Refusal
        );
        Assert.False(
            condition: lane.IsAvailable,
            userMessage: "two failed connects must enter backoff"
        );

        Volatile.Write(
            location: ref peerIsUp,
            value: true
        );

        var served = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        await serverTask;

        Assert.True(
            condition: served.Ok,
            userMessage: served.Failure.ToString()
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a success inside the backoff window must clear the window at once"
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

                    observedOrder.Add(item: request.Body.Span[0]);
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
                actual: Assert.Single(collection: results[index].Body.ToArray())
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
    /// <summary>A peer that takes the request and then never writes a byte is answered
    /// <see cref="WireRefusal.RequestTimedOut"/> once the per-request deadline expires — inside the ten seconds a
    /// consumer waits on the task, the bound <c>WorldRemoteAuthority</c> relies on — with the request written exactly
    /// once, no reconnect, no second route sample, and the lane still available: a silent peer is neither an absent
    /// one nor a reason to apply the request twice. Falsifier: bounding the read by the lifetime alone (no per-request
    /// deadline) parks the worker until the runner's own budget; re-sending on expiry makes the listener see a second
    /// connection and the route a second sample.</summary>
    [Fact]
    public async Task SilentPeerAfterTheRequestWasWritten_AnswersRequestTimedOut_WithoutResendOrBackoff() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        var requestTimeout = TimeSpan.FromMilliseconds(value: 300);
        var framesSeen = 0;
        var routeReads = 0;
        var acceptedAgain = false;

        using var deadline = Laws.SocketDeadline();
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
        var serverTask = Task.Run(
            function: async () => {
                // The silent peer: takes every frame the lane writes and never answers; the connection ends only when
                // the lane drops it, which is how the frame count becomes final.
                using (var client = await listener.AcceptTcpClientAsync(cancellationToken: deadline.Token)) {
                    var stream = client.GetStream();

                    await HandshakeWireFormat.TryReadExactAsync(
                        buffer: new byte[HandshakeWireFormat.HelloBytes],
                        ct: deadline.Token,
                        stream: stream
                    );

                    try {
                        while ((await WireFrame.ReadAsync(
                            stream: stream,
                            maxFrameBytes: 4096,
                            ct: deadline.Token
                        )).Ok) {
                            Interlocked.Increment(location: ref framesSeen);
                        }
                    } catch (IOException) {
                        // A reset instead of a clean close is the same end of the connection.
                    }
                }

                try {
                    using var second = await listener.AcceptTcpClientAsync(cancellationToken: watch.Token);

                    acceptedAgain = true;
                } catch (OperationCanceledException) {
                    // Nothing dialed again before the answer arrived — the passing outcome.
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol(),
            requestTimeout: requestTimeout,
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
            body: [1],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 10)
        );

        watch.Cancel();
        await serverTask;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.RequestTimedOut,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "is not re-sent"
        );
        Assert.Equal(
            expected: 1,
            actual: Volatile.Read(location: ref framesSeen)
        );
        Assert.Equal(
            expected: 1,
            actual: Volatile.Read(location: ref routeReads)
        );
        Assert.False(
            condition: acceptedAgain,
            userMessage: "a timed-out request must never be re-sent over a fresh connection"
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a silent peer is not an absent one; the lane must not enter backoff"
        );
    }
    /// <summary>A request write that never completes — the dialect parks in it, as it would against a peer that took
    /// the Hello and then stopped reading — is answered <see cref="WireRefusal.RequestTimedOut"/> once the per-request
    /// deadline expires, with a detail saying the write did not complete, never one claiming the request was written,
    /// over exactly one connection, with no re-send and the lane still available: a stalled reader is neither an
    /// absent peer nor a connect failure. Falsifier: narrating every deadline expiry past <c>EnsureConnectedAsync</c>
    /// as "the request was written" tells the caller the peer holds a request it never received whole; routing the
    /// write's expiry to the connect-failure path dials a second time and enters backoff.</summary>
    [Fact]
    public async Task SilentPeerDuringTheRequestWrite_AnswersRequestTimedOut_SayingTheWriteDidNotComplete() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        var requestTimeout = TimeSpan.FromMilliseconds(value: 300);
        var accepted = 0;

        using var deadline = Laws.SocketDeadline();
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
        var serverTask = Task.Run(
            function: async () => {
                // The stalled reader: accepts every connection, takes its Hello, and never reads again; the connections
                // are held open until the answer has arrived, and the count is how a second dial would show.
                var held = new List<TcpClient>();

                try {
                    while (true) {
                        var client = await listener.AcceptTcpClientAsync(cancellationToken: watch.Token);

                        held.Add(item: client);
                        Interlocked.Increment(location: ref accepted);
                        await HandshakeWireFormat.TryReadExactAsync(
                            buffer: new byte[HandshakeWireFormat.HelloBytes],
                            ct: watch.Token,
                            stream: client.GetStream()
                        );
                    }
                } catch (OperationCanceledException) {
                    // The answer arrived and the law stopped counting.
                } finally {
                    foreach (var client in held) {
                        client.Dispose();
                    }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol { StallsRequestWrite = true },
            requestTimeout: requestTimeout,
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var response = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 10)
        );

        watch.Cancel();
        await serverTask;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.RequestTimedOut,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "the request write did not complete"
        );
        Assert.DoesNotContain(
            actualString: response.Failure.Detail,
            expectedSubstring: "the request was written"
        );
        Assert.Equal(
            expected: 1,
            actual: Volatile.Read(location: ref accepted)
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a stalled reader is not an absent peer; the lane must not enter backoff"
        );
    }
    /// <summary>A peer that accepts the connection and the Hello but never completes authentication is a connect
    /// failure bounded by the per-request deadline: two attempts each time out, the lane declares itself unreachable
    /// (<see cref="WireRefusal.LaneUnavailable"/> naming the timeout, backoff entered) and the answer arrives in about
    /// twice the deadline — never a third connection. Falsifier: leaving <c>EnsureConnectedAsync</c> outside the
    /// attempt deadline parks the worker in the stalled authentication until the runner's own budget.</summary>
    [Fact]
    public async Task StallInsideAuthenticate_DeclaresUnreachableAfterTwoTimedOutAttempts() {
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        var endpoint = ((IPEndPoint)listener.LocalEndpoint);
        var requestTimeout = TimeSpan.FromMilliseconds(value: 500);
        var accepted = 0;

        using var deadline = Laws.SocketDeadline();
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
        var serverTask = Task.Run(
            function: async () => {
                // Accepts every connection and holds it open without ever writing; the stall itself is the dialect's.
                var held = new List<TcpClient>();

                try {
                    while (true) {
                        held.Add(item: await listener.AcceptTcpClientAsync(cancellationToken: watch.Token));
                        Interlocked.Increment(location: ref accepted);
                    }
                } catch (OperationCanceledException) {
                    // The answer arrived and the law stopped counting.
                } finally {
                    foreach (var client in held) {
                        client.Dispose();
                    }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        using var lane = new PersistentRequestLane<FakeRequestKind, FakeResponseKind>(
            connectRetryDelay: TimeSpan.FromMilliseconds(value: 5),
            lifetime: deadline.Token,
            protocol: new FakeLaneProtocol { StallsAuthentication = true },
            requestTimeout: requestTimeout,
            route: () => new LaneRoute(
                Endpoint: endpoint,
                Description: endpoint.ToString()
            ),
            sourceAuthority: "test-authority",
            unavailableBackoff: TimeSpan.FromSeconds(value: 30)
        );

        var stopwatch = Stopwatch.StartNew();
        var response = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        ).WaitAsync(
            cancellationToken: deadline.Token,
            timeout: TimeSpan.FromSeconds(value: 10)
        );
        var elapsed = stopwatch.Elapsed;

        watch.Cancel();
        await serverTask;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: nameof(TimeoutException)
        );
        Assert.Equal(
            expected: 2,
            actual: Volatile.Read(location: ref accepted)
        );
        // Two deadlines back to back plus the retry delay; the slack absorbs scheduling, never a third attempt.
        Assert.InRange(
            actual: elapsed,
            high: ((2 * requestTimeout) + TimeSpan.FromSeconds(value: 2)),
            low: requestTimeout
        );
        Assert.False(
            condition: lane.IsAvailable,
            userMessage: "two timed-out connects must enter backoff"
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
            requestTimeout: TimeSpan.FromSeconds(value: 10),
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
