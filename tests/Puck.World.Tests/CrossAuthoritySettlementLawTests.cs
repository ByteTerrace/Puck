using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class CrossAuthoritySettlementLawTests {
    [Fact]
    public void ReversingTheSamePairStillSelectsExactlyOneOwner() {
        var first = new WorldEntityAddress(Authority: "world/alpha", Generation: 3, Index: 7);
        var second = new WorldEntityAddress(Authority: "world/beta", Generation: 11, Index: 2);

        var firstResponds = WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", local: in first, remote: in second);
        var secondResponds = WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", local: in second, remote: in first);

        Assert.NotEqual(actual: secondResponds, expected: firstResponds);
    }
    [Fact]
    public void ARepeatedPairIsStickyAndASelfPairNeverResponds() {
        var first = new WorldEntityAddress(Authority: "world/alpha", Generation: 3, Index: 7);
        var second = new WorldEntityAddress(Authority: "world/beta", Generation: 11, Index: 2);
        var expected = WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", local: in first, remote: in second);

        for (var attempt = 0; (attempt < 32); attempt++) {
            Assert.Equal(expected: expected, actual: WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", local: in first, remote: in second));
        }
        Assert.False(condition: WorldCrossAuthoritySettlement.LocalResponds(interaction: "physical-contact", local: in first, remote: in first));
    }
}
