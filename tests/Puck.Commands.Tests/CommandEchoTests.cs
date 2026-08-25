using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the shared echo grammar: a segment is either a run of <see cref="CommandEcho.Field(string, string)"/>
/// <c>key=value</c> tokens, or one <see cref="CommandEcho.Head(string)"/> word followed by qualifying
/// <see cref="CommandEcho.Field(string, string)"/> tokens — <c>[verb: key=value key=value | head field=value]</c> —
/// with a <see cref="CommandEcho.Segment"/> boundary's separator written only when further content follows, never
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
    public void SegmentBetweenHeadFieldGroupsInsertsThePipeOnlyWhenSomethingFollows() {
        // The world.groups shape: each segment is a declared Head word followed by qualifying Field tokens.
        var line = CommandEcho.Open(verb: "world.groups")
            .Head(head: "kind").Field(key: "name", value: "party")
            .Segment()
            .Head(head: "group").Field(key: "id", value: "alpha").Field(key: "kind", value: "party")
            .Segment()
            .Close();

        // The trailing Segment() before Close() vanishes rather than leaving a bare "| ]".
        Assert.Equal(actual: line, expected: "[world.groups: kind name=party | group id=alpha kind=party]");
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
    [Fact]
    public void HeadThenFieldsFormsOneSegmentDistinctFromAPureFieldSegment() {
        // world.market's own two segment shapes side by side: config is pure Field tokens (no Head), a listing opens
        // with the declared Head "listing".
        var line = CommandEcho.Open(verb: "world.market")
            .Field(key: "feeBasisPoints", value: 1000)
            .Segment()
            .Head(head: "listing").Field(key: "id", value: 1).Field(key: "status", value: "Active")
            .Close();

        Assert.Equal(actual: line, expected: "[world.market: feeBasisPoints=1000 | listing id=1 status=Active]");
    }
}
