using Puck.Networking.Peers;
using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>Two sides that refuse each other must not both sit out <see cref="PeerWireProtocol.HandshakeTimeout"/>
/// waiting for the other to close: each writes its refusal, drains for at most
/// <see cref="PeerWireProtocol.RefusalDrainTimeout"/> on its own clock, and closes, so the whole exchange ends once
/// that drain elapses. Both peers run on one <see cref="VirtualClock"/>, so the law is stated in clock time: the
/// exchange is still open when both drains are armed, and advancing exactly
/// <see cref="PeerWireProtocol.RefusalDrainTimeout"/> ends it with every handshake deadline still unexpired.</summary>
public sealed class MutualRefusalTests {
    [Fact]
    public async Task BothSidesRefusingChannelUnbound_StopAfterTheRefusalDrain_NeverWaitingOutTheHandshakeTimeout() {
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualClock();
        using var certificateOwnerA = PeerIdentity.Create();
        using var certificateOwnerB = PeerIdentity.Create();

        await using var peerA = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwnerA,
            identity: PeerIdentity.Create(),
            timeProvider: clock
        );
        await using var peerB = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwnerB,
            identity: PeerIdentity.Create(),
            timeProvider: clock
        );

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var dialing = peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var refusedAtB = PeerTestSupport.NextHandshakeRefusalAsync(peer: peerB).AsTask();
        var bothDraining = clock.WhenArmedAsync(
            count: 2,
            ct: ct,
            dueTime: PeerWireProtocol.RefusalDrainTimeout
        );

        // Neither side can close before its own drain ends, so the exchange cannot finish before both drains are
        // armed on the clock; a drain timed by anything else would let it finish first.
        Assert.Same(
            expected: bothDraining,
            actual: await Task.WhenAny(
                task1: bothDraining,
                task2: Task.WhenAll(
                    dialing,
                    refusedAtB
                )
            )
        );
        Assert.Equal(
            expected: 2,
            actual: clock.Armed(dueTime: PeerWireProtocol.HandshakeTimeout)
        );

        clock.Advance(by: PeerWireProtocol.RefusalDrainTimeout);

        var atA = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing);
        var atB = await refusedAtB;

        // Each side reads the other's offer before anything else, so each decides ChannelUnbound for itself
        // rather than learning it from the other's refusal frame.
        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atA.Failure.Refusal
        );
        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atB.Failure.Refusal
        );
        Assert.Equal(
            expected: PeerWireProtocol.RefusalDrainTimeout,
            actual: clock.Elapsed
        );
        Assert.Empty(collection: peerA.Links);
        Assert.Empty(collection: peerB.Links);
    }
}
