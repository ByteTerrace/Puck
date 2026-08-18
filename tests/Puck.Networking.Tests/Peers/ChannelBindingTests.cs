using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>The identity a peer offers must be the key its transport proved possession of; a certificate minted
/// from another key is refused by name on the side that observes it, the far side is told the same name, and no
/// link exists on either side afterwards.</summary>
public sealed class ChannelBindingTests {
    private static void AssertToldOfRefusal(PeerFailure farSide, PeerRefusal refusal) {
        Assert.Equal(
            expected: PeerRefusal.RefusedByPeer,
            actual: farSide.Refusal
        );
        Assert.Contains(
            actualString: farSide.Detail,
            expectedSubstring: refusal.ToString(),
            comparisonType: StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AcceptorWhoseCertificateIsNotItsIdentity_IsRefusedByTheDialer_AsChannelUnbound() {
        var ct = TestContext.Current.CancellationToken;

        using var certificateOwner = PeerIdentity.Create();

        await using var peerA = PeerTestSupport.NewPeer();
        await using var peerB = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwner,
            identity: PeerIdentity.Create()
        );

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);

        var atA = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        ));

        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atA.Failure.Refusal
        );

        var atB = await PeerTestSupport.NextHandshakeRefusalAsync(peer: peerB);

        AssertToldOfRefusal(
            farSide: atB.Failure,
            refusal: PeerRefusal.ChannelUnbound
        );
        Assert.Empty(collection: peerA.Links);
        Assert.Empty(collection: peerB.Links);
    }
    [Fact]
    public async Task DialerWhoseCertificateIsNotItsIdentity_IsRefusedByTheAcceptor_AsChannelUnbound() {
        var ct = TestContext.Current.CancellationToken;

        using var certificateOwner = PeerIdentity.Create();

        await using var peerA = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwner,
            identity: PeerIdentity.Create()
        );
        await using var peerB = PeerTestSupport.NewPeer();

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);

        var atA = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        ));
        var atB = await PeerTestSupport.NextHandshakeRefusalAsync(peer: peerB);

        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atB.Failure.Refusal
        );
        AssertToldOfRefusal(
            farSide: atA.Failure,
            refusal: PeerRefusal.ChannelUnbound
        );
        Assert.Empty(collection: peerA.Links);
        Assert.Empty(collection: peerB.Links);
    }
}
