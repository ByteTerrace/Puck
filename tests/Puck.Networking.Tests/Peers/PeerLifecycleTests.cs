using System.Diagnostics;
using Puck.Networking.Peers;
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
    private const int MessagesSentToAnUnreadLink = 100;
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
    public async Task PeerDisposeAsync_WhenTheRemoteNeverAcknowledgesTheStreamShutdown_CompletesWithinTheSocketBudget_AndThePeerObservesConnectionClosed() {
        using var deadline = Laws.SocketDeadline();

        var identityA = PeerIdentity.Create();
        var identityB = PeerIdentity.Create();

        var (connectionAtA, connectionAtB) = InMemoryPeerConnection.Pair(
            keyProvedByA: identityA.SubjectPublicKeyInfo,
            keyProvedByB: identityB.SubjectPublicKeyInfo
        );
        var transportB = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials from B"));

        await using var peerB = new Peer(
            identity: identityB,
            transport: transportB
        );

        var peerA = new Peer(
            identity: identityA,
            transport: new FakePeerTransport(dial: _ => connectionAtA)
        );

        await peerB.ListenAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback()
        );
        transportB.Accept(connection: connectionAtB);

        var linkAtoB = await peerA.DialAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback(port: 2)
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: deadline.Token);

        Assert.Equal(
            expected: identityB.Id.Domain,
            actual: linkAtoB.RemoteId.Domain
        );

        // Nothing ever calls connectionAtA.AcknowledgeShutdowns(): the remote has vanished as far as A's stream
        // shutdown is concerned, so a close that waited for it would sit until the deadline. The connection is
        // closed first, and that is what lets the stream's dispose finish.
        var clock = Stopwatch.StartNew();

        await peerA.DisposeAsync().AsTask().WaitAsync(cancellationToken: deadline.Token);

        clock.Stop();

        Assert.True(
            condition: (clock.Elapsed < Laws.SocketBudget),
            userMessage: $"disposing the peer took {clock.Elapsed}"
        );
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
    public async Task PeerDisposeAsync_WhileConnectionsAreStillBeingAccepted_ReturnsOnlyOnceEveryAcceptedConnectionIsDisposed() {
        using var deadline = Laws.SocketDeadline();

        var transport = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials"));
        var peer = new Peer(
            identity: PeerIdentity.Create(),
            transport: transport
        );

        await peer.ListenAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback()
        );

        // Connections keep arriving while the peer is disposed, so the accept loop is mid-accept — a connection
        // taken but not yet counted as a handshake — at some point close to the disposal. Each one parks in its
        // handshake (no control stream ever opens), and disposal must not return while any it took is live.
        var offering = Task.Run(
            cancellationToken: deadline.Token,
            function: async () => {
                for (var i = 0; (i < ConnectionsOfferedDuringDisposal); i++) {
                    transport.Accept(connection: new SilentPeerConnection());

                    await Task.Yield();
                }
            }
        );

        await Task.Yield();
        await peer.DisposeAsync().AsTask().WaitAsync(cancellationToken: deadline.Token);
        await offering.WaitAsync(cancellationToken: deadline.Token);

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
    public async Task PeerDisposeAsync_WhileADialIsInFlight_UnwindsTheDialAsDisposed_BeforeReturning() {
        using var deadline = Laws.SocketDeadline();

        var connection = new SilentPeerConnection();
        var peer = new Peer(
            identity: PeerIdentity.Create(),
            transport: new FakePeerTransport(dial: _ => connection)
        );

        // The silent stream swallows the offer and answers nothing, so the dial is parked inside its handshake by
        // the time DialAsync hands back its task.
        var dialing = peer.DialAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback(port: 1)
        );
        var clock = Stopwatch.StartNew();

        await peer.DisposeAsync().AsTask().WaitAsync(cancellationToken: deadline.Token);

        clock.Stop();

        // Disposal waited for the dial to unwind — its connection is already released when DisposeAsync returns —
        // and did not wait out the handshake clock to do it.
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "the peer was disposed while its dial still held the connection"
        );
        Assert.True(
            condition: (clock.Elapsed < PeerWireProtocol.HandshakeTimeout),
            userMessage: $"disposing the peer took {clock.Elapsed}; the dial should have been cancelled, not timed out at {PeerWireProtocol.HandshakeTimeout}"
        );

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing.WaitAsync(cancellationToken: deadline.Token));

        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: thrown.Failure.Refusal
        );
        Assert.Empty(collection: peer.Links);
    }
    [Fact]
    public async Task PeerDisposeAsync_WhileASendLoopRuns_CompletesWithinTheSocketBudget_AndTheLoopIsRefusedAsConnectionClosed() {
        using var deadline = Laws.SocketDeadline();

        var (peerA, peerB, linkAtoB, _) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);

        await using var disposeB = peerB;

        // The receiving side never reads, so once its event channel fills the sender's writes back up into the
        // transport's flow control: the loop is either mid-write or about to write when the peer is disposed.
        var enoughSent = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = SendUntilRefusedAsync(
            ct: deadline.Token,
            enoughSent: enoughSent,
            link: linkAtoB
        );

        await enoughSent.Task.WaitAsync(cancellationToken: deadline.Token);
        await peerA.DisposeAsync().AsTask().WaitAsync(cancellationToken: deadline.Token);

        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => sending.WaitAsync(cancellationToken: deadline.Token));

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
    public async Task PeerDisposeAsync_WhileTheEventsConsumerNeverReads_CompletesWithinTheSocketBudget_AndCloseFailureIsDisposed() {
        using var deadline = Laws.SocketDeadline();

        var (peerA, peerB, linkAtoB, linkBtoA) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);

        await using var disposeA = peerA;

        for (var i = 0; (i < MessagesSentToAnUnreadLink); i++) {
            await linkAtoB.SendAsync(
                ct: deadline.Token,
                payload: "never read"u8.ToArray()
            );
        }

        // Nobody reads linkBtoA.Events, so its read loop fills the channel and then parks on the next publish; the
        // law disposes the peer in exactly that state.
        await PeerTestSupport.WaitUntilAsync(
            condition: () => (linkBtoA.Events.Count == PeerLink.EventsCapacity),
            ct: deadline.Token
        );
        await peerB.DisposeAsync().AsTask().WaitAsync(cancellationToken: deadline.Token);

        Assert.False(condition: linkBtoA.IsOpen);
        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: linkBtoA.CloseFailure.Refusal
        );
        // What the consumer finds when it finally reads: the events that filled the channel, then completion (a
        // channel's Completion settles only once it is both closed and drained), and no Closed — that one was dropped
        // because the channel was full, which is exactly why CloseFailure exists.
        var pending = new List<PeerEvent>();

        await foreach (var @event in linkBtoA.Events.ReadAllAsync(cancellationToken: deadline.Token)) {
            pending.Add(item: @event);
        }

        await linkBtoA.Events.Completion.WaitAsync(cancellationToken: deadline.Token);

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
