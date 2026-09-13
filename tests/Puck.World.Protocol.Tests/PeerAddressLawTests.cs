using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>A wire address is independent of an authority's authored local-seat reservation.</summary>
public sealed class PeerAddressLawTests {
    [Fact]
    public void EveryRepresentablePeerAddressRoundTripsWithoutBecomingASeat() {
        for (var index = 0; (index < WorldBodiesLimits.CapacityCeiling); index++) {
            var principal = WorldPrincipal.Peer(
                generation: 7,
                index: index
            );

            Assert.True(condition: WorldPrincipal.TryParse(
                principal.Describe(),
                out var parsed
            ));
            Assert.Equal(
                actual: parsed,
                expected: principal
            );
            var grant = new WorldGrant(
                Principal: principal,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.All,
                Exclusive: false
            );

            Assert.True(
                condition: WorldSubmissionCodec.TryEncodeGrant(
                    bytes: out var bytes,
                    failure: out var failure,
                    grant: grant
                ),
                userMessage: failure.ToString()
            );
            Assert.True(
                condition: WorldSubmissionCodec.TryDecodeGrant(
                    bytes: bytes,
                    failure: out failure,
                    grant: out var decoded
                ),
                userMessage: failure.ToString()
            );
            Assert.Equal(
                actual: decoded,
                expected: grant
            );
            Assert.Equal(
                PrincipalKind.Peer,
                decoded.Principal.Kind
            );
        }
    }
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [Theory]
    public void InvalidPeerAddressesAndGenerationsRemainRefused(int index, int generation) {
        var principal = WorldPrincipal.Peer(
            generation: generation,
            index: index
        );

        Assert.False(condition: WorldPrincipal.TryParse(
            principal.Describe(),
            out _
        ));
        var grant = new WorldGrant(
            Principal: principal,
            Capability: WorldCapability.Control,
            Subject: GrantSubject.All,
            Exclusive: false
        );

        Assert.False(condition: WorldSubmissionCodec.TryEncodeGrant(
            bytes: out _,
            failure: out var failure,
            grant: grant
        ));
        Assert.Equal(
            WorldCodecRefusal.PrincipalShapeInvalid,
            failure.Refusal
        );
    }
}
