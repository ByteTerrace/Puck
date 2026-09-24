using Puck.Networking.Peers;
using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A <see cref="Peer"/> listens at most once, and its disposal completes no matter what its links or dials
/// are in the middle of: a send loop is refused rather than left pending, a link whose consumer stopped reading is
/// closed as <see cref="PeerRefusal.Disposed"/> even though its read loop is parked on a full
/// <see cref="PeerLink.Events"/> channel, a dial in flight is unwound as <see cref="PeerRefusal.Disposed"/> before
/// the identity it signs with is disposed, a dial after disposal is refused at entry, a remote that never
/// acknowledges a stream shutdown stalls nothing, and no connection the accept loop took is still live when
/// disposal returns, however close to the accept the disposal landed.</summary>
public sealed class PeerLifecycleTests {
    private const int ConnectionsOfferedDuringDisposal = 32;
    private const int SendsBeforeDisposal = 4;

    /// <summary>Sends the largest admissible payload over and over until the link refuses, signalling
    /// <paramref name="enoughSent"/> once <see cref="SendsBeforeDisposal"/> sends have completed. Ends only by the
    /// exception the refused send throws.</summary>
    private static async Task SendUntilRefusedAsync(PeerLink link, TaskCompletionSource enoughSent, CancellationToken ct) {
        var payload = new byte[PeerWireProtocol.MaxMessagePayloadBytes];
        var sent = 0;

        while (true) {
            await link.SendAsync(
                ct: ct,
                payload: payload
            );

            sent++;

            if (sent == SendsBeforeDisposal) {
                enoughSent.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task DialAsync_AfterDispose_ThrowsObjectDisposedException_WithoutTouchingTheTransport() {
        var peer = new Peer(
            identity: PeerIdentity.Create(),
            transport: new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "a dial after disposal must never reach the transport"))
        );

        await peer.DisposeAsync();

        // The transport's own exception would surface as HandshakeFaulted or escape as InvalidOperationException;
        // ObjectDisposedException proves the dial was refused before the transport was asked for anything.
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => peer.DialAsync(
            ct: TestContext.Current.CancellationToken,
            endpoint: PeerTestSupport.Loopback(port: 1)
        ));
    }
    [Fact]
    public async Task ListenAsync_Twice_ThrowsInvalidOperationException_AndKeepsTheFirstListener() {
        await using var peer = PeerTestSupport.NewPeer();

        var endpoint = await PeerTestSupport.ListenLoopbackAsync(peer: peer);

        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => peer.ListenAsync(
            ct: TestContext.Current.CancellationToken,
            endpoint: PeerTestSupport.Loopback()
        ));

        Assert.Equal(
            expected: endpoint,
            actual: peer.ListenEndpoint
        );
        Assert.Null(@object: peer.ListenerFault);
    }
    [Fact]
    public async Task PeerDisposeAsync_WhenTheRemoteNeverAcknowledgesTheStreamShutdown_NeverWaitsForIt_AndThePeerObservesConnectionClosed() {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, linkBtoA, connectionAtA, connectionAtB) = await PeerTestSupport.ConnectInMemoryAsync(ct: ct);

        await using var disposeB = peerB;

        // Nothing calls connectionAtA.AcknowledgeShutdowns() while the law runs: the remote has vanished as far as
        // A's stream shutdown is concerned. The connection is closed first, and that is what lets the stream's
        // dispose finish; a stream shut down while the connection is still open parks on the acknowledgement
        // instead, and the connection reports that before the dispose could ever return.
        var disposing = peerA.DisposeAsync().AsTask();

        try {
            Assert.Same(
                expected: disposing,
                actual: await Task.WhenAny(
                    task1: disposing,
                    task2: connectionAtA.ShutdownParked
                ).WaitAsync(cancellationToken: ct)
            );
        } finally {
            // Whatever the verdict, the law is over: release any shutdown still parked so both peers can be torn down.
            connectionAtA.AcknowledgeShutdowns();
            connectionAtB.AcknowledgeShutdowns();
        }

        await disposing;
        Assert.True(condition: connectionAtA.IsDisposed);
        Assert.False(condition: linkAtoB.IsOpen);
        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: linkAtoB.CloseFailure.Refusal
        );
        Assert.Empty(collection: peerA.Links);

        var closed = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: closed.Failure.Refusal
        );
    }
    [Fact]
    public async Task PeerDisposeAsync_WhileADialIsInFlight_UnwindsTheDialAsDisposed_BeforeReturning() {
        var ct = TestContext.Current.CancellationToken;
        // Never advanced: no handshake deadline can expire, so whatever ends the dial is the disposal.
        var clock = new VirtualClock();

        var connection = new SilentPeerConnection();
        var peer = new Peer(
            identity: PeerIdentity.Create(),
            timeProvider: clock,
            transport: new FakePeerTransport(dial: _ => connection)
        );

        // The silent stream swallows the offer and answers nothing, so the dial parks inside its handshake, under a
        // deadline armed on the clock.
        var dialing = peer.DialAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback(port: 1)
        );

        await clock.WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: PeerWireProtocol.HandshakeTimeout
        );
        await peer.DisposeAsync().AsTask().WaitAsync(cancellationToken: ct);

        // Disposal waited for the dial to unwind — its connection is already released when DisposeAsync returns.
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "the peer was disposed while its dial still held the connection"
        );

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing.WaitAsync(cancellationToken: ct));

        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: thrown.Failure.Refusal
        );
        Assert.Equal(
            expected: TimeSpan.Zero,
            actual: clock.Elapsed
        );
        Assert.Empty(collection: peer.Links);
    }
    [Fact]
    public async Task PeerDisposeAsync_WhileASendLoopRuns_Completes_AndTheLoopIsRefusedAsConnectionClosed() {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, _) = await PeerTestSupport.ConnectAsync(ct: ct);

        await using var disposeB = peerB;

        // The receiving side never reads, so once its event channel fills the sender's writes back up into the
        // transport's flow control: the loop is either mid-write or about to write when the peer is disposed.
        var enoughSent = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = SendUntilRefusedAsync(
            ct: ct,
            enoughSent: enoughSent,
            link: linkAtoB
        );

        await enoughSent.Task.WaitAsync(cancellationToken: ct);
        await peerA.DisposeAsync().AsTask().WaitAsync(cancellationToken: ct);

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => sending.WaitAsync(cancellationToken: ct));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: thrown.Failure.Refusal
        );
        Assert.False(condition: linkAtoB.IsOpen);
        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: linkAtoB.CloseFailure.Refusal
        );
        Assert.Empty(collection: peerA.Links);
    }
    [Fact]
    public async Task PeerDisposeAsync_WhileConnectionsAreStillBeingAccepted_ReturnsOnlyOnceEveryAcceptedConnectionIsDisposed() {
        var ct = TestContext.Current.CancellationToken;

        var transport = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials"));
        var peer = new Peer(
            identity: PeerIdentity.Create(),
            transport: transport
        );

        await peer.ListenAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback()
        );

        // Connections keep arriving while the peer is disposed, so the accept loop is mid-accept — a connection
        // taken but not yet counted as a handshake — at some point close to the disposal. Each one parks in its
        // handshake (no control stream ever opens), and disposal must not return while any it took is live.
        var offering = Task.Run(
            cancellationToken: ct,
            function: async () => {
                for (var i = 0; (i < ConnectionsOfferedDuringDisposal); i++) {
                    transport.Accept(connection: new SilentPeerConnection());

                    await Task.Yield();
                }
            }
        );

        await Task.Yield();
        await peer.DisposeAsync().AsTask().WaitAsync(cancellationToken: ct);
        await offering.WaitAsync(cancellationToken: ct);

        Assert.Null(@object: peer.ListenerFault);

        // Nothing here waits: a taken connection the drain gate missed would be disposed only after this assertion,
        // by a handshake still running against a disposed peer.
        Assert.All(
            action: static connection => Assert.True(
                condition: Assert.IsType<SilentPeerConnection>(@object: connection).IsDisposed,
                userMessage: "the peer's disposal returned while a connection its accept loop took was still live"
            ),
            collection: transport.Taken
        );
    }
    [Fact]
    public async Task PeerDisposeAsync_WhileTheEventsConsumerNeverReads_CompletesWithCloseFailureDisposed() {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, linkBtoA, connectionAtA, connectionAtB) = await PeerTestSupport.ConnectInMemoryAsync(ct: ct);

        await using var disposeA = peerA;

        for (var i = 0; (i <= PeerLink.EventsCapacity); i++) {
            await linkAtoB.SendAsync(
                ct: ct,
                payload: "never read"u8.ToArray()
            );
        }

        // Nobody reads linkBtoA.Events. Once B has read every byte A wrote, its read loop has taken one message more
        // than the channel holds, so it is parked on that publish; the law disposes the peer in exactly that state.
        await connectionAtB.ReadThroughAsync(
            bytes: connectionAtA.BytesWritten,
            ct: ct
        );
        Assert.Equal(
            expected: PeerLink.EventsCapacity,
            actual: linkBtoA.Events.Count
        );
        await peerB.DisposeAsync().AsTask().WaitAsync(cancellationToken: ct);

        Assert.False(condition: linkBtoA.IsOpen);
        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: linkBtoA.CloseFailure.Refusal
        );
        // What the consumer finds when it finally reads: the events that filled the channel, then completion (a
        // channel's Completion settles only once it is both closed and drained), and no Closed — that one was dropped
        // because the channel was full, which is exactly why CloseFailure exists.
        var pending = new List<PeerEvent>();

        await foreach (var @event in linkBtoA.Events.ReadAllAsync(cancellationToken: ct)) {
            pending.Add(item: @event);
        }

        await linkBtoA.Events.Completion.WaitAsync(cancellationToken: ct);

        Assert.Equal(
            expected: PeerLink.EventsCapacity,
            actual: pending.Count
        );
        Assert.All(
            action: static @event => Assert.IsType<PeerEvent.Received>(@object: @event),
            collection: pending
        );
    }
}
