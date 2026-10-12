using System.Net;
using System.Net.Sockets;
using Puck.Testing;
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
/// <summary>A count a law awaits rather than polls: <see cref="ReachedAsync"/> completes once the count reaches a
/// target, bounded only by the test's own token.</summary>
file sealed class Tally {
    private readonly Lock m_lock = new();
    private TaskCompletionSource m_changed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private int m_count;

    public int Count {
        get {
            lock (m_lock) {
                return m_count;
            }
        }
    }

    public void Increment() {
        TaskCompletionSource changed;

        lock (m_lock) {
            m_count++;
            changed = m_changed;
            m_changed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
    public async Task ReachedAsync(int count) {
        while (true) {
            Task changed;

            lock (m_lock) {
                if (m_count >= count) {
                    return;
                }

                changed = m_changed.Task;
            }

            await changed.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }
}
/// <summary>A minimal <see cref="ILaneProtocol{TRequestKind,TResponseKind}"/> riding the real
/// <see cref="HandshakeWireFormat"/>/<see cref="WireFrame"/> primitives, so these laws exercise the same wire
/// grammar a production dialect would. It counts each step it enters, so a law can wait until the lane has reached
/// the step whose deadline it is about to expire.</summary>
file sealed class FakeLaneProtocol : ILaneProtocol<FakeRequestKind, FakeResponseKind> {
    /// <summary>Gets the count of <see cref="AuthenticateAsync"/> calls.</summary>
    public Tally Authentications { get; } = new();

    /// <summary>Gets or sets how many further <see cref="ReadResponseAsync"/> calls throw an
    /// <see cref="InvalidOperationException"/> before reading a byte — an exception outside the wire vocabulary, the
    /// shape a dialect's own bug takes.</summary>
    public int ReadResponseFaultsRemaining { get; set; }

    /// <summary>Gets the count of <see cref="WriteRequestAsync"/> calls.</summary>
    public Tally RequestWrites { get; } = new();
    /// <summary>Gets the count of <see cref="ReadResponseAsync"/> calls — each one proves the request it answers was
    /// written in full.</summary>
    public Tally ResponseReads { get; } = new();

    /// <summary>Gets a value indicating whether <see cref="AuthenticateAsync"/> parks until its token is cancelled —
    /// a peer that accepts the connection and the Hello and then never completes the exchange.</summary>
    public bool StallsAuthentication { get; init; }
    /// <summary>Gets a value indicating whether <see cref="WriteRequestAsync"/> parks until its token is cancelled
    /// without writing a byte — a request write that never completes, the shape a peer whose receive window is full
    /// gives the dialect.</summary>
    public bool StallsRequestWrite { get; init; }

    public Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct) {
        Authentications.Increment();

        return (StallsAuthentication
            ? Task.Delay(
                cancellationToken: ct,
                millisecondsDelay: Timeout.Infinite
            )
            : Task.CompletedTask
        );
    }
    public bool MayResend(FakeRequestKind kind) => (kind switch {
        FakeRequestKind.Submission => false,
        _ => true,
    });
    public async Task<LaneResponse<FakeResponseKind>> ReadResponseAsync(Stream stream, CancellationToken ct) {
        ResponseReads.Increment();

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
    public Task WriteRequestAsync(Stream stream, FakeRequestKind kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        RequestWrites.Increment();

        return (StallsRequestWrite
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
}
/// <summary>The lane's transport seam over loopback TCP, counted: every connect the lane asks for, the endpoint it
/// named, and the stream it got back — so "never dialed again" and "the socket was released" are facts a law reads
/// off the seam rather than races it runs against a listener. Port zero is a fixture marker, never dialed: it is
/// refused through the same seam without waiting on the OS's SYN retry policy or racing another test for a recently
/// released ephemeral port. A dialer built with an <c>admitted</c> count refuses every connect past it the same way,
/// so a law that forbids a second connection gets a prompt answer it can count, never a request parked on a socket
/// nobody serves.</summary>
file sealed class LoopbackDialer(int admitted = int.MaxValue) {
    private readonly List<EndPoint> m_dialed = [];
    private readonly Lock m_lock = new();
    private readonly List<Stream> m_streams = [];

    public Tally Connects { get; } = new();

    public IReadOnlyList<EndPoint> Dialed {
        get {
            lock (m_lock) {
                return [.. m_dialed];
            }
        }
    }
    public IReadOnlyList<Stream> Streams {
        get {
            lock (m_lock) {
                return [.. m_streams];
            }
        }
    }

    public async ValueTask<Stream> ConnectAsync(EndPoint endpoint, CancellationToken ct) {
        int ordinal;

        lock (m_lock) {
            m_dialed.Add(item: endpoint);
            ordinal = m_dialed.Count;
        }

        Connects.Increment();

        if ((endpoint is IPEndPoint { Port: 0 }) || (ordinal > admitted)) {
            throw new SocketException(errorCode: ((int)SocketError.ConnectionRefused));
        }

        var socket = new Socket(
            protocolType: ProtocolType.Tcp,
            socketType: SocketType.Stream
        ) { NoDelay = true };

        try {
            await socket.ConnectAsync(
                cancellationToken: ct,
                remoteEP: endpoint
            );
        } catch {
            socket.Dispose();

            throw;
        }

        var stream = new NetworkStream(
            ownsSocket: true,
            socket: socket
        );

        lock (m_lock) {
            m_streams.Add(item: stream);
        }

        return stream;
    }
}
/// <summary>Builds the lane every law drives: the counted dialer's seam, a <see cref="VirtualClock"/> nobody but the
/// law advances, no connect retry delay unless a law asks for one, and the test's own token as the lifetime.</summary>
file static class Lanes {
    public static PersistentRequestLane<FakeRequestKind, FakeResponseKind> NewLane(Func<LaneRoute> route, LoopbackDialer dialer, VirtualClock? clock = null, FakeLaneProtocol? protocol = null, CancellationToken? lifetime = null, Action<Exception>? onUnavailable = null, TimeSpan? connectRetryDelay = null, TimeSpan? requestTimeout = null, TimeSpan? unavailableBackoff = null) => new(
        connect: dialer.ConnectAsync,
        connectRetryDelay: (connectRetryDelay ?? TimeSpan.Zero),
        lifetime: (lifetime ?? TestContext.Current.CancellationToken),
        onUnavailable: onUnavailable,
        protocol: (protocol ?? new FakeLaneProtocol()),
        requestTimeout: (requestTimeout ?? PersistentRequestLaneLawTests.RequestTimeout),
        route: route,
        sourceAuthority: "test-authority",
        timeProvider: (clock ?? new VirtualClock()),
        unavailableBackoff: (unavailableBackoff ?? PersistentRequestLaneLawTests.Backoff)
    );
}

/// <summary>
/// Laws for <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> — the state machine
/// <c>WorldRemoteAuthority</c>'s federation lanes ride. Every scenario drives the real class over a real loopback
/// socket through a counted <see cref="LoopbackDialer"/>, on a <see cref="VirtualClock"/> the law alone advances: the
/// per-request deadline, the connect retry delay, and the backoff window elapse only when a law says so, so no verdict
/// here depends on how long anything took.
/// </summary>
public sealed class PersistentRequestLaneLawTests {
    internal static readonly TimeSpan Backoff = TimeSpan.FromSeconds(value: 30);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(value: 10);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>Accepts one connection and reads its Hello; the caller owns the client.</summary>
    private static async Task<(TcpClient Client, NetworkStream Stream)> AcceptHelloAsync(TcpListener listener) {
        var client = await listener.AcceptTcpClientAsync(cancellationToken: TestToken);
        var stream = client.GetStream();

        await HandshakeWireFormat.TryReadExactAsync(
            buffer: new byte[HandshakeWireFormat.HelloBytes],
            ct: TestToken,
            stream: stream
        );

        return (client, stream);
    }
    /// <summary>Reads one request and answers it with a Pong echoing its body.</summary>
    private static async Task EchoAsync(Stream stream) {
        var request = await ReadRequestAsync(stream: stream);

        await WireFrame.WriteAsync(
            body: request.Body,
            ct: TestToken,
            kind: ((byte)FakeResponseKind.Pong),
            stream: stream
        );
    }
    private static TcpListener Listen() {
        var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();

        return listener;
    }
    private static Task<WireFrameRead> ReadRequestAsync(Stream stream) => WireFrame.ReadAsync(
        ct: TestToken,
        maxFrameBytes: 4096,
        stream: stream
    );
    private static Func<LaneRoute> RouteTo(EndPoint endpoint) => () => new LaneRoute(
        Description: endpoint.ToString()!,
        Endpoint: endpoint
    );
    private static IPEndPoint UnreachableEndpoint() => new(
        address: IPAddress.Loopback,
        port: 0
    );

    /// <summary>A break on an already-established connection reconnects and re-sends exactly once, without ever
    /// calling the lane unreachable. Falsifier: dropping the <c>hadConnection</c> branch (always taking the
    /// "report immediately" arm) turns the second request's answer into a refusal instead of a successful retry.</summary>
    [Fact]
    public async Task BreakOnEstablishedConnection_ReconnectsAndResendsOnce_WithoutEnteringBackoff() {
        using var listener = Listen();
        var dialer = new LoopbackDialer();
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                // First connection: answers request #1, then reads request #2 and closes without answering (the break).
                var (first, stream) = await AcceptHelloAsync(listener: listener);

                using (first) {
                    await EchoAsync(stream: stream);
                    _ = await ReadRequestAsync(stream: stream);
                }

                // Second connection: the resend of request #2 lands here and gets a real answer.
                var (second, secondStream) = await AcceptHelloAsync(listener: listener);

                using (second) {
                    await EchoAsync(stream: secondStream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            route: RouteTo(endpoint: listener.LocalEndpoint)
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
        Assert.Equal(
            expected: 2,
            actual: dialer.Connects.Count
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a break on a live connection must not enter backoff"
        );
    }
    /// <summary>A break on an established connection while a kind the protocol refuses to re-send is in flight is
    /// answered <see cref="WireRefusal.ConnectionClosed"/> with the request left in doubt: the lane dials exactly once,
    /// and never enters backoff. Falsifier: skipping the
    /// <see cref="ILaneProtocol{TRequestKind,TResponseKind}.MayResend"/> check on the break arm re-sends the request
    /// over a fresh connection, which the dialer counts before the answer can arrive.</summary>
    [Fact]
    public async Task BreakOnEstablishedConnection_WhenTheKindMayNotBeResent_AnswersConnectionClosed_AndSendsExactlyOnce() {
        using var listener = Listen();
        var dialer = new LoopbackDialer(admitted: 1);
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                // Answers the ping, then reads the submission and closes without a reply (the break).
                var (client, stream) = await AcceptHelloAsync(listener: listener);

                using (client) {
                    await EchoAsync(stream: stream);
                    _ = await ReadRequestAsync(stream: stream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            route: RouteTo(endpoint: listener.LocalEndpoint)
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
        Assert.Equal(
            expected: 1,
            actual: dialer.Connects.Count
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "an in-doubt request must not enter backoff"
        );
    }
    /// <summary>A connect failure is retried once, exactly <c>connectRetryDelay</c> later on the lane's clock, and the
    /// second failure declares the lane unreachable — never a third attempt. Falsifier: changing the
    /// <c>++connectFailures &gt;= 2</c> threshold to 3 dials a third time; retrying without the delay dials the second
    /// time before the clock has moved.</summary>
    [Fact]
    public async Task ConnectFailure_RetriesOnceAfterTheRetryDelay_ThenDeclaresUnreachable_NeverThree() {
        var clock = new VirtualClock();
        var dialer = new LoopbackDialer();
        var retryDelay = TimeSpan.FromMilliseconds(value: 5);

        using var lane = Lanes.NewLane(
            clock: clock,
            connectRetryDelay: retryDelay,
            dialer: dialer,
            route: RouteTo(endpoint: UnreachableEndpoint())
        );

        var answer = lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        var retryArmed = clock.WhenArmedAsync(
            count: 1,
            ct: TestToken,
            dueTime: retryDelay
        );

        Assert.Same(
            expected: retryArmed,
            actual: await Task.WhenAny(
                task1: retryArmed,
                task2: answer
            )
        );
        Assert.Equal(
            expected: 1,
            actual: dialer.Connects.Count
        );

        clock.Advance(by: retryDelay);

        // A third attempt would arm the retry delay again rather than answer.
        var thirdAttempt = clock.WhenArmedAsync(
            count: 1,
            ct: TestToken,
            dueTime: retryDelay
        );

        Assert.Same(
            expected: answer,
            actual: await Task.WhenAny(
                task1: answer,
                task2: thirdAttempt
            )
        );

        var response = await answer;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Equal(
            expected: 2,
            actual: dialer.Connects.Count
        );
        Assert.False(
            condition: lane.IsAvailable,
            userMessage: "two failed connects must enter backoff"
        );
    }
    /// <summary>A connect attempt samples its route exactly once — the same snapshot serves the reconnect-needed check,
    /// the socket it dials, and the description recorded for that socket — so one attempt never pairs one route
    /// generation's endpoint with another's description. The route here answers its first read with a live endpoint
    /// and every later read with another generation. Falsifier: splitting the read into two calls (one to decide the
    /// endpoint, a later one to record its description) pushes the count above one, and a leaked second read reaches
    /// the dialer as the other generation's endpoint.</summary>
    [Fact]
    public async Task Connect_SamplesTheRouteExactlyOnce_PerAttempt_AndDialsOnlyThatGeneration() {
        using var listener = Listen();
        var endpointA = ((IPEndPoint)listener.LocalEndpoint);
        var endpointB = new IPEndPoint(
            address: IPAddress.Loopback,
            port: endpointA.Port ^ 1
        );
        var dialer = new LoopbackDialer();
        var routeReads = 0;
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                var (client, stream) = await AcceptHelloAsync(listener: listener);

                using (client) {
                    await EchoAsync(stream: stream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            route: () => ((Interlocked.Increment(location: ref routeReads) == 1)
                ? new LaneRoute(
                    Description: endpointA.ToString(),
                    Endpoint: endpointA
                )
                : new LaneRoute(
                    Description: endpointB.ToString(),
                    Endpoint: endpointB
                )
            )
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
        Assert.Equal(
            expected: [endpointA],
            actual: dialer.Dialed
        );
    }
    /// <summary>A <c>connectRetryDelay</c> or <c>requestTimeout</c> outside [0, 1 day] is refused by the constructor,
    /// naming the parameter, before any worker starts; the bounds themselves are admitted. Falsifier: storing the
    /// values unchecked lets a negative <c>requestTimeout</c> reach the per-attempt deadline, which answers every
    /// request <see cref="WireRefusal.LaneUnavailable"/> naming <see cref="ArgumentOutOfRangeException"/> without ever
    /// touching a socket, and then makes <c>Dispose</c> throw from its bounded join — the one exception its catch does
    /// not cover.</summary>
    [Fact]
    public void Constructor_RefusesATimingOutsideItsRange_ByName() {
        var dialer = new LoopbackDialer();

        PersistentRequestLane<FakeRequestKind, FakeResponseKind> Build(TimeSpan connectRetryDelay, TimeSpan requestTimeout) => Lanes.NewLane(
            connectRetryDelay: connectRetryDelay,
            dialer: dialer,
            requestTimeout: requestTimeout,
            route: RouteTo(endpoint: UnreachableEndpoint())
        );

        var refusals = new (TimeSpan ConnectRetryDelay, TimeSpan RequestTimeout, string ParamName)[] {
            (TimeSpan.Zero, TimeSpan.FromSeconds(value: -5), "requestTimeout"),
            (TimeSpan.Zero, (TimeSpan.FromDays(value: 1) + TimeSpan.FromTicks(value: 1)), "requestTimeout"),
            (TimeSpan.FromTicks(value: -1), RequestTimeout, "connectRetryDelay"),
            ((TimeSpan.FromDays(value: 1) + TimeSpan.FromTicks(value: 1)), RequestTimeout, "connectRetryDelay"),
        };

        foreach (var (connectRetryDelay, requestTimeout, paramName) in refusals) {
            Assert.Equal(
                expected: paramName,
                actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Build(
                    connectRetryDelay: connectRetryDelay,
                    requestTimeout: requestTimeout
                )).ParamName
            );
        }

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
    /// <summary>An <c>onUnavailable</c> callback that disposes the lane gets a real join — it runs on the thread pool,
    /// never on the worker that <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Dispose"/> joins — so by
    /// the time its <c>Dispose</c> returns the worker has stopped, and the request that raised it still gets its named
    /// refusal. Falsifier: invoking the callback on the worker makes <c>Dispose</c> join the worker from the worker,
    /// a join that can only be abandoned, so the worker is still running when <c>Dispose</c> returns.</summary>
    [Fact]
    public async Task Dispose_FromInsideOnUnavailable_JoinsTheStoppedWorker() {
        var callbackReturned = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        PersistentRequestLane<FakeRequestKind, FakeResponseKind>? lane = null;

        lane = Lanes.NewLane(
            dialer: new LoopbackDialer(),
            onUnavailable: _ => {
                lane!.Dispose();
                callbackReturned.TrySetResult(result: lane.Completion.IsCompleted);
            },
            route: RouteTo(endpoint: UnreachableEndpoint())
        );

        try {
            var response = await lane.Enqueue(
                body: [],
                kind: FakeRequestKind.Ping
            ).WaitAsync(cancellationToken: TestToken);

            Assert.True(
                condition: await callbackReturned.Task.WaitAsync(cancellationToken: TestToken),
                userMessage: "Dispose returned from inside onUnavailable while the worker was still running"
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
        var lane = Lanes.NewLane(
            dialer: new LoopbackDialer(),
            route: RouteTo(endpoint: UnreachableEndpoint())
        );

        lane.Dispose();
        lane.Dispose();
    }
    /// <summary>Once the lane stops — by <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Dispose"/>, or by
    /// its lifetime token with no <c>Dispose</c> in between, the shape a host's shutdown token takes — its worker's
    /// exit has already released the socket it connected (the peer parked on its next read sees
    /// <see cref="WireRefusal.ConnectionClosed"/>), and a request queued afterwards is answered
    /// <see cref="WireRefusal.LaneUnavailable"/> before <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Enqueue"/>
    /// returns, even on a lane that served traffic. The law waits on
    /// <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Completion"/>, so the late request meets the closed
    /// queue rather than the exit drain. Falsifiers: dropping the socket only in <c>Dispose</c> leaves the
    /// lifetime-stopped lane's stream open after its worker stopped; closing the queue only in <c>Dispose</c> accepts
    /// the lifetime-stopped lane's late write, and with no worker left to drain it the task is still pending.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AfterTheLaneStops_TheSocketIsReleased_AndALateEnqueueIsAnsweredLaneUnavailableAtOnce(bool byDispose) {
        using var listener = Listen();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: TestToken);
        var dialer = new LoopbackDialer();
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                var (client, stream) = await AcceptHelloAsync(listener: listener);

                using (client) {
                    await EchoAsync(stream: stream);

                    // The lane's next move is to leave; a peer parked on the next frame sees that as a prefix EOF.
                    return await ReadRequestAsync(stream: stream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            lifetime: lifetime.Token,
            route: RouteTo(endpoint: listener.LocalEndpoint)
        );

        var served = await lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        Assert.True(
            condition: served.Ok,
            userMessage: served.Failure.ToString()
        );

        if (byDispose) {
            lane.Dispose();
        } else {
            lifetime.Cancel();
        }

        await lane.Completion.WaitAsync(cancellationToken: TestToken);

        Assert.False(
            condition: Assert.Single(collection: dialer.Streams).CanRead,
            userMessage: "the worker stopped without releasing the socket it connected"
        );

        var afterClose = await serverTask;

        Assert.False(condition: afterClose.Ok);
        Assert.Equal(
            expected: WireRefusal.ConnectionClosed,
            actual: afterClose.Failure.Refusal
        );

        var late = lane.Enqueue(
            body: [2],
            kind: FakeRequestKind.Ping
        );

        Assert.True(
            condition: late.IsCompleted,
            userMessage: "a request queued after the lane stopped must be answered before Enqueue returns"
        );

        var response = await late;

        Assert.Equal(
            expected: WireRefusal.LaneUnavailable,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "is closed"
        );
    }
    /// <summary>A protocol that throws outside the wire vocabulary mid-exchange costs only that request: it is
    /// answered <see cref="WireRefusal.LaneUnavailable"/> naming the exception, the connection it happened on is
    /// dropped, the lane does not enter backoff, and the worker survives to serve the next request over a fresh
    /// connection. Falsifier: removing the catch-all around <c>ServeAsync</c> faults the worker on the first request,
    /// which then completes <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Completion"/> without ever
    /// answering the request.</summary>
    [Fact]
    public async Task ProtocolExceptionOutsideTheWireVocabulary_AnswersLaneUnavailable_AndKeepsWorkerAliveForTheNextRequest() {
        using var listener = Listen();
        var dialer = new LoopbackDialer();
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                // First connection: the request arrives, but the dialect throws before it reads any reply, so none is
                // written; the lane drops this socket.
                var (first, stream) = await AcceptHelloAsync(listener: listener);

                using (first) {
                    _ = await ReadRequestAsync(stream: stream);
                }

                // Second connection: the next request lands on a fresh socket and gets a real answer.
                var (second, secondStream) = await AcceptHelloAsync(listener: listener);

                using (second) {
                    await EchoAsync(stream: secondStream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            protocol: new FakeLaneProtocol { ReadResponseFaultsRemaining = 1 },
            route: RouteTo(endpoint: listener.LocalEndpoint)
        );

        var answer = lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        // A worker the exception killed would complete before it ever answered.
        Assert.Same(
            expected: answer,
            actual: await Task.WhenAny(
                task1: answer,
                task2: lane.Completion
            ).WaitAsync(cancellationToken: TestToken)
        );

        var first = await answer;

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
        ).WaitAsync(cancellationToken: TestToken);

        await serverTask;

        Assert.True(
            condition: second.Ok,
            userMessage: second.Failure.ToString()
        );
        Assert.Equal(
            expected: ((byte)2),
            actual: Assert.Single(collection: second.Body.ToArray())
        );
        Assert.Equal(
            expected: 2,
            actual: dialer.Connects.Count
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a dialect's own exception is the request's answer, never a backoff"
        );
    }
    /// <summary>A request that succeeds while the lane is inside its unreachable backoff clears that backoff at once:
    /// <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.IsAvailable"/> reports <see langword="true"/> again
    /// with the lane's clock never having moved, so nothing but the success can have cleared it. Falsifier: resetting
    /// only the noted flag (not the backoff deadline) on success leaves it <see langword="false"/>.</summary>
    [Fact]
    public async Task QueuedSuccess_ResetsAvailability_InsideTheBackoffWindow() {
        using var listener = Listen();
        var clock = new VirtualClock();
        var unreachable = UnreachableEndpoint();
        var reachable = listener.LocalEndpoint;
        var peerIsUp = false;
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                var (client, stream) = await AcceptHelloAsync(listener: listener);

                using (client) {
                    await EchoAsync(stream: stream);
                }
            }
        );

        using var lane = Lanes.NewLane(
            clock: clock,
            dialer: new LoopbackDialer(),
            // The route is republished from the absent peer to the live one between the two requests; the worker
            // reads it after the queue handoff, which orders the write below before this read.
            route: () => {
                var endpoint = (Volatile.Read(location: ref peerIsUp)
                    ? reachable
                    : unreachable
                );

                return new LaneRoute(
                    Description: endpoint.ToString()!,
                    Endpoint: endpoint
                );
            }
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
        Assert.Equal(
            expected: TimeSpan.Zero,
            actual: clock.Elapsed
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
        const int RequestCount = 16;

        using var listener = Listen();
        var dialer = new LoopbackDialer();
        var observedOrder = new List<int>();
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                var (client, stream) = await AcceptHelloAsync(listener: listener);

                using (client) {
                    for (var index = 0; (index < RequestCount); index++) {
                        var request = await ReadRequestAsync(stream: stream);

                        observedOrder.Add(item: request.Body.Span[0]);
                        await WireFrame.WriteAsync(
                            body: request.Body,
                            ct: TestToken,
                            kind: ((byte)FakeResponseKind.Pong),
                            stream: stream
                        );
                    }
                }
            }
        );

        using var lane = Lanes.NewLane(
            dialer: dialer,
            route: RouteTo(endpoint: listener.LocalEndpoint)
        );

        var pending = new Task<LaneResponse<FakeResponseKind>>[RequestCount];

        for (var index = 0; (index < RequestCount); index++) {
            pending[index] = lane.Enqueue(
                body: [((byte)index)],
                kind: FakeRequestKind.Ping
            );
        }

        var results = await Task.WhenAll(tasks: pending);

        await serverTask;

        for (var index = 0; (index < RequestCount); index++) {
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
                count: RequestCount,
                start: 0
            ),
            actual: observedOrder
        );
        Assert.Equal(
            expected: 1,
            actual: dialer.Connects.Count
        );
    }
    /// <summary>A peer that goes silent once the lane has reached the request write — either never answering a request
    /// it took whole, or never taking the write at all (a full receive window) — is answered
    /// <see cref="WireRefusal.RequestTimedOut"/> at exactly the per-request deadline on the lane's clock: one tick
    /// short of it the request is still pending. The detail says whether the write completed, the request crossed the
    /// wire at most once over exactly one connection with one route sample, and the lane is still available: a silent
    /// peer is neither an absent one nor a reason to apply the request twice. Falsifiers: bounding the exchange by the
    /// lifetime alone (no per-request deadline) leaves the request pending after the deadline; narrating every expiry
    /// past <c>EnsureConnectedAsync</c> as "the request was written" misnames the stalled write; routing the expiry to
    /// the connect-failure path dials a second time and enters backoff.</summary>
    [Theory]
    [InlineData(false, "the request was written", "did not complete", 1)]
    [InlineData(true, "the request write did not complete", "the request was written", 0)]
    public async Task SilentPeer_AnswersRequestTimedOut_AtExactlyTheDeadline_WithoutResendOrBackoff(bool stallsWrite, string detail, string forbiddenDetail, int framesDelivered) {
        using var listener = Listen();
        var clock = new VirtualClock();
        var dialer = new LoopbackDialer(admitted: 1);
        var protocol = new FakeLaneProtocol { StallsRequestWrite = stallsWrite };
        var routeReads = 0;
        var serverTask = Task.Run(
            cancellationToken: TestToken,
            function: async () => {
                // The silent peer takes every frame the lane delivers and never answers; the connection ends only when
                // the lane drops it, which is what makes the frame count final.
                var (client, stream) = await AcceptHelloAsync(listener: listener);
                var frames = 0;

                using (client) {
                    try {
                        while ((await ReadRequestAsync(stream: stream)).Ok) {
                            frames++;
                        }
                    } catch (IOException) {
                        // A reset instead of a clean close is the same end of the connection.
                    }
                }

                return frames;
            }
        );

        using var lane = Lanes.NewLane(
            clock: clock,
            dialer: dialer,
            protocol: protocol,
            route: () => {
                Interlocked.Increment(location: ref routeReads);

                return new LaneRoute(
                    Description: listener.LocalEndpoint.ToString()!,
                    Endpoint: listener.LocalEndpoint
                );
            }
        );

        var answer = lane.Enqueue(
            body: [1],
            kind: FakeRequestKind.Ping
        );

        await (stallsWrite ? protocol.RequestWrites : protocol.ResponseReads).ReachedAsync(count: 1);
        clock.Advance(by: (RequestTimeout - TimeSpan.FromTicks(value: 1)));
        Assert.False(
            condition: answer.IsCompleted,
            userMessage: "the request was answered before its deadline"
        );
        Assert.Equal(
            expected: 1,
            actual: clock.Armed(dueTime: RequestTimeout)
        );

        clock.Advance(by: TimeSpan.FromTicks(value: 1));

        var response = await answer;

        Assert.False(condition: response.Ok);
        Assert.Equal(
            expected: WireRefusal.RequestTimedOut,
            actual: response.Failure.Refusal
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: detail
        );
        Assert.Contains(
            actualString: response.Failure.Detail,
            expectedSubstring: "is not re-sent"
        );
        Assert.DoesNotContain(
            actualString: response.Failure.Detail,
            expectedSubstring: forbiddenDetail
        );
        Assert.Equal(
            expected: framesDelivered,
            actual: await serverTask
        );
        Assert.Equal(
            expected: 1,
            actual: dialer.Connects.Count
        );
        Assert.Equal(
            expected: 1,
            actual: Volatile.Read(location: ref routeReads)
        );
        Assert.True(
            condition: lane.IsAvailable,
            userMessage: "a silent peer is not an absent one; the lane must not enter backoff"
        );
    }
    /// <summary>A peer that accepts the connection and the Hello but never completes authentication is a connect
    /// failure bounded by the per-request deadline: each of two attempts parks in authentication until its own
    /// deadline fires on the lane's clock, and the second expiry declares the lane unreachable
    /// (<see cref="WireRefusal.LaneUnavailable"/> naming the timeout, backoff entered) after exactly two deadlines of
    /// clock time — never a third connection. Falsifier: leaving <c>EnsureConnectedAsync</c> outside the attempt
    /// deadline leaves the first attempt parked when the clock fires.</summary>
    [Fact]
    public async Task StallInsideAuthenticate_DeclaresUnreachableAfterTwoTimedOutAttempts() {
        // The listener's backlog completes each connect and buffers each Hello; nothing ever answers, and the stall
        // itself is the dialect's.
        using var listener = Listen();
        var clock = new VirtualClock();
        var dialer = new LoopbackDialer();
        var protocol = new FakeLaneProtocol { StallsAuthentication = true };

        using var lane = Lanes.NewLane(
            clock: clock,
            dialer: dialer,
            protocol: protocol,
            route: RouteTo(endpoint: listener.LocalEndpoint)
        );

        var answer = lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        await protocol.Authentications.ReachedAsync(count: 1);
        clock.Advance(by: RequestTimeout);

        // The first expiry is a connect failure, so it retries rather than answers.
        var secondAttempt = protocol.Authentications.ReachedAsync(count: 2);

        Assert.Same(
            expected: secondAttempt,
            actual: await Task.WhenAny(
                task1: secondAttempt,
                task2: answer
            )
        );
        clock.Advance(by: RequestTimeout);

        // The second expiry answers rather than dialing a third time.
        var thirdAttempt = protocol.Authentications.ReachedAsync(count: 3);

        Assert.Same(
            expected: answer,
            actual: await Task.WhenAny(
                task1: answer,
                task2: thirdAttempt
            )
        );

        var response = await answer;

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
            actual: dialer.Connects.Count
        );
        Assert.Equal(
            expected: (2 * RequestTimeout),
            actual: clock.Elapsed
        );
        Assert.False(
            condition: lane.IsAvailable,
            userMessage: "two timed-out connects must enter backoff"
        );
    }
    /// <summary>A throwing <c>onUnavailable</c> callback is contained: the request whose connect failure raised it
    /// still gets its own refusal — the unreachable peer, never the callback's exception — and the worker survives to
    /// serve a later <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.Enqueue"/>. Falsifier: invoking the
    /// callback inline without containment lets its exception escape <c>Unreachable</c>, so the request is answered
    /// with the callback's failure instead of the peer's.</summary>
    [Fact]
    public async Task ThrowingOnUnavailableCallback_FailsCurrentRequest_AndKeepsWorkerAliveForLaterEnqueues() {
        using var lane = Lanes.NewLane(
            dialer: new LoopbackDialer(),
            onUnavailable: _ => throw new InvalidOperationException(message: "the callback itself is broken"),
            route: RouteTo(endpoint: UnreachableEndpoint())
        );

        for (var request = 0; (request < 2); request++) {
            var response = await lane.Enqueue(
                body: [],
                kind: FakeRequestKind.Ping
            ).WaitAsync(cancellationToken: TestToken);

            Assert.False(condition: response.Ok);
            Assert.Equal(
                expected: WireRefusal.LaneUnavailable,
                actual: response.Failure.Refusal
            );
            Assert.Contains(
                actualString: response.Failure.Detail,
                expectedSubstring: "is unreachable"
            );
        }

        Assert.False(condition: lane.Completion.IsCompleted);
    }
    /// <summary>Once unreachable, the lane reports <see cref="PersistentRequestLane{TRequestKind,TResponseKind}.IsAvailable"/>
    /// as <see langword="false"/> for exactly its configured backoff window on its clock — still unavailable one tick
    /// before the window closes, available at the tick it closes.</summary>
    [Fact]
    public async Task Unreachable_StaysUnavailableForTheBackoffWindow_ThenRecovers() {
        var clock = new VirtualClock();
        var backoff = TimeSpan.FromMilliseconds(value: 200);

        using var lane = Lanes.NewLane(
            clock: clock,
            dialer: new LoopbackDialer(),
            route: RouteTo(endpoint: UnreachableEndpoint()),
            unavailableBackoff: backoff
        );

        _ = await lane.Enqueue(
            body: [],
            kind: FakeRequestKind.Ping
        );

        Assert.False(condition: lane.IsAvailable);

        clock.Advance(by: (backoff - TimeSpan.FromTicks(value: 1)));

        Assert.False(condition: lane.IsAvailable);

        clock.Advance(by: TimeSpan.FromTicks(value: 1));

        Assert.True(condition: lane.IsAvailable);
    }
}
