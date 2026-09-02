using System.Diagnostics;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A send the peer never grants stream credit for is ended by <see cref="PeerWireProtocol.SendTimeout"/>,
/// the link's own clock rather than the caller's: at expiry the link closes as <see cref="PeerRefusal.ConnectionClosed"/>
/// with the clock's name in the detail, the stalled send and every send queued behind it are refused by that name,
/// and nothing about the caller's token decides any of it. Runs over an in-memory connection pair, because loopback
/// QUIC cannot be made to withhold credit on cue.</summary>
public sealed class SendTimeoutTests {
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(value: 2);

    [Fact]
    public async Task SendAsync_WhenThePeerWithholdsStreamCredit_ClosesTheLinkAsConnectionClosed_AtTheSendTimeout_AndReleasesTheSendQueuedBehindIt() {
        using var deadline = Laws.SocketDeadline();

        var identityA = PeerIdentity.Create();
        var identityB = PeerIdentity.Create();

        var (connectionAtA, connectionAtB) = InMemoryPeerConnection.Pair(
            keyProvedByA: identityA.SubjectPublicKeyInfo,
            keyProvedByB: identityB.SubjectPublicKeyInfo
        );
        var transportB = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials from B"));

        await using var peerA = new Peer(
            identity: identityA,
            transport: new FakePeerTransport(dial: _ => connectionAtA)
        );
        await using var peerB = new Peer(
            identity: identityB,
            transport: transportB
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

        // The handshake and the link are honest up to here; from now on B grants A no credit, so A's next write
        // parks inside the transport with the write gate held, and the send behind it parks on the gate.
        connectionAtA.WithholdWriteCredit();

        var clock = Stopwatch.StartNew();
        var stalled = linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "never credited"u8.ToArray()
        );
        var queued = linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "behind the stalled send"u8.ToArray()
        );
        var stalledRefusal = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => stalled.WaitAsync(cancellationToken: deadline.Token));
        var queuedRefusal = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => queued.WaitAsync(cancellationToken: deadline.Token));

        clock.Stop();

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: stalledRefusal.Failure.Refusal
        );
        Assert.Contains(
            actualString: stalledRefusal.Failure.Detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(PeerWireProtocol.SendTimeout)
        );
        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: queuedRefusal.Failure.Refusal
        );
        // The caller's deadline (Laws.SocketBudget) is far longer than the send clock, so an exit inside the clock
        // plus slack is the link's own doing.
        Assert.True(
            condition: (clock.Elapsed < (PeerWireProtocol.SendTimeout + Slack)),
            userMessage: $"the stalled send took {clock.Elapsed}; the send clock is {PeerWireProtocol.SendTimeout}"
        );
        Assert.False(condition: linkAtoB.IsOpen);
        Assert.Equal(
            expected: stalledRefusal.Failure,
            actual: linkAtoB.CloseFailure
        );

        var closed = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: linkAtoB.CloseFailure,
            actual: closed.Failure
        );
        Assert.True(condition: connectionAtA.IsDisposed);

        // A send after the close is refused at entry by the same name, and the far side sees a closed connection,
        // not a partial frame it could mistake for anything else.
        var late = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => linkAtoB.SendAsync(
            ct: deadline.Token,
            payload: "too late"u8.ToArray()
        ));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: late.Failure.Refusal
        );

        var closedAtB = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkBtoA));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: closedAtB.Failure.Refusal
        );
    }
}
