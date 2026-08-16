using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class CrossAuthoritySettlementLawTests {
    [Fact]
    public void ReversingTheSamePairStillSelectsExactlyOneOwner() {
        var first = new WorldEntityAddress(Authority: "world/alpha", Index: 7, Generation: 3);
        var second = new WorldEntityAddress(Authority: "world/beta", Index: 2, Generation: 11);

        var firstResponds = WorldCrossAuthoritySettlement.LocalResponds(local: in first, remote: in second, interaction: "physical-contact");
        var secondResponds = WorldCrossAuthoritySettlement.LocalResponds(local: in second, remote: in first, interaction: "physical-contact");

        Assert.NotEqual(actual: secondResponds, expected: firstResponds);
    }
    [Fact]
    public void ARepeatedPairIsStickyAndASelfPairNeverResponds() {
        var first = new WorldEntityAddress(Authority: "world/alpha", Index: 7, Generation: 3);
        var second = new WorldEntityAddress(Authority: "world/beta", Index: 2, Generation: 11);
        var expected = WorldCrossAuthoritySettlement.LocalResponds(local: in first, remote: in second, interaction: "physical-contact");

        for (var attempt = 0; (attempt < 32); attempt++) {
            Assert.Equal(expected: expected, actual: WorldCrossAuthoritySettlement.LocalResponds(local: in first, remote: in second, interaction: "physical-contact"));
        }
        Assert.False(condition: WorldCrossAuthoritySettlement.LocalResponds(local: in first, remote: in first, interaction: "physical-contact"));
    }
}
