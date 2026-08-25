using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the shared echo grammar: <c>[verb: key=value key=value | key=value]</c>, with a
/// <see cref="CommandEcho.Segment"/> boundary's separator written only when further content follows — never
/// trailing.</summary>
public sealed class CommandEchoTests {
    [Fact]
    public void OpenFieldCloseProducesTheBracketedLine() {
        var line = CommandEcho.Open(verb: "world.status")
            .Field(key: "kits", value: 1)
            .Field(key: "dirty", value: false)
            .Close();

        Assert.Equal(actual: line, expected: "[world.status: kits=1 dirty=false]");
    }
    [Fact]
    public void SegmentBetweenGroupsInsertsThePipeOnlyWhenSomethingFollows() {
        var line = CommandEcho.Open(verb: "world.groups")
            .Text(text: "kind party roles=[]")
            .Segment()
            .Text(text: "group alpha kind=party")
            .Segment()
            .Close();

        // The trailing Segment() before Close() vanishes rather than leaving a bare "| ]".
        Assert.Equal(actual: line, expected: "[world.groups: kind party roles=[] | group alpha kind=party]");
    }
    [Fact]
    public void ASegmentFollowedByAFieldNeverTrails() {
        var line = CommandEcho.Open(verb: "world.contributions")
            .Text(text: "slot 'a' state=empty")
            .Segment()
            .Field(key: "tick", value: 3)
            .Close();

        Assert.Equal(actual: line, expected: "[world.contributions: slot 'a' state=empty | tick=3]");
    }
    [Fact]
    public void NoContentClosesToAnEmptyBody() {
        Assert.Equal(actual: CommandEcho.Open(verb: "world.market").Close(), expected: "[world.market:]");
    }
}
