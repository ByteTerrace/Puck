using Xunit;

namespace Puck.World.Server.Tests;

public sealed class CrossAuthoritySettlementLawTests {
    [Fact]
    public void ARepeatedPairIsStickyAndASelfPairNeverResponds() {
        var expected = WorldCrossAuthoritySettlement.LocalResponds(
            interaction: "physical-contact",
            localAuthority: "world/alpha",
            remoteAuthority: "world/beta"
        );

        for (var attempt = 0; (attempt < 32); attempt++) {
            Assert.Equal(
                expected: expected,
                actual: WorldCrossAuthoritySettlement.LocalResponds(
                    interaction: "physical-contact",
                    localAuthority: "world/alpha",
                    remoteAuthority: "world/beta"
                )
            );
        }
        Assert.False(condition: WorldCrossAuthoritySettlement.LocalResponds(
            interaction: "physical-contact",
            localAuthority: "world/alpha",
            remoteAuthority: "world/alpha"
        ));
    }
    [Theory]
    [InlineData("world/alpha", "world/beta")]
    [InlineData("a", "b")]
    [InlineData("local", "remote")]
    public void ReversingTheSamePairStillSelectsExactlyOneOwner(string first, string second) {
        Assert.NotEqual(
            expected: WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", localAuthority: first, remoteAuthority: second),
            actual: WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", localAuthority: second, remoteAuthority: first)
        );
    }
}
