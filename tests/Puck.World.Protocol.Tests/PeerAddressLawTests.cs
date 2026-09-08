using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>A wire address is independent of an authority's authored local-seat reservation.</summary>
public sealed class PeerAddressLawTests {
    [Fact]
    public void EveryRepresentablePeerAddressRoundTripsWithoutBecomingASeat() {
        for (var index = 0; index < WorldBodiesLimits.CapacityCeiling; index++) {
            var principal = WorldPrincipal.Peer(index: index, generation: 7);
            Assert.True(WorldPrincipal.TryParse(principal.Describe(), out var parsed));
            Assert.Equal(principal, parsed);
            var grant = new WorldGrant(Principal: principal, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false);
            Assert.True(WorldSubmissionCodec.TryEncodeGrant(grant, out var bytes, out var failure), failure.ToString());
            Assert.True(WorldSubmissionCodec.TryDecodeGrant(bytes, out var decoded, out failure), failure.ToString());
            Assert.Equal(grant, decoded);
            Assert.Equal(PrincipalKind.Peer, decoded.Principal.Kind);
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void InvalidPeerAddressesAndGenerationsRemainRefused(int index, int generation) {
        var principal = WorldPrincipal.Peer(index: index, generation: generation);
        Assert.False(WorldPrincipal.TryParse(principal.Describe(), out _));
        var grant = new WorldGrant(Principal: principal, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false);
        Assert.False(WorldSubmissionCodec.TryEncodeGrant(grant, out _, out var failure));
        Assert.Equal(WorldCodecRefusal.PrincipalShapeInvalid, failure.Refusal);
    }
}
