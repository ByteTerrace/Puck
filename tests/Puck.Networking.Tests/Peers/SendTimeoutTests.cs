using Puck.Testing;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A send the peer never grants stream credit for is ended by <see cref="PeerWireProtocol.SendTimeout"/>,
/// the link's own clock rather than the caller's: at expiry the link closes as <see cref="PeerRefusal.ConnectionClosed"/>
/// with the clock's name in the detail, the stalled send and every send queued behind it are refused by that name,
/// and nothing about the caller's token decides any of it. Runs over an in-memory connection pair, because loopback
/// QUIC cannot be made to withhold credit on cue.</summary>
public sealed class SendTimeoutTests {
    [Fact]
    public async Task SendAsync_WhenThePeerWithholdsStreamCredit_ClosesTheLinkAsConnectionClosed_AtTheSendTimeout_AndReleasesTheSendQueuedBehindIt() {
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualClock();

        var (peerA, peerB, linkAtoB, linkBtoA, connectionAtA, _) = await PeerTestSupport.ConnectInMemoryAsync(
            clockA: clock,
            ct: ct
        );

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        // The handshake and the link are honest up to here; from now on B grants A no credit, so A's next write
        // parks inside the transport with the write gate held, and the send behind it parks on the gate.
        connectionAtA.WithholdWriteCredit();

        var stalled = linkAtoB.SendAsync(
            ct: ct,
            payload: "never credited"u8.ToArray()
        );
        var queued = linkAtoB.SendAsync(
            ct: ct,
            payload: "behind the stalled send"u8.ToArray()
        );

        Assert.False(condition: stalled.IsCompleted);
        Assert.False(condition: queued.IsCompleted);
        await clock.WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: PeerWireProtocol.SendTimeout
        );
        clock.Advance(by: PeerWireProtocol.SendTimeout);
        var stalledRefusal = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => stalled.WaitAsync(cancellationToken: ct));
        var queuedRefusal = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => queued.WaitAsync(cancellationToken: ct));

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
            ct: ct,
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
