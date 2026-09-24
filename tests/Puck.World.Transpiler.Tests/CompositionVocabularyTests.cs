using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The composition surface is described by the same catalogue as ordinary document constructs, while
/// the older <c>state { world { } }</c> spelling keeps its contextual identity.</summary>
public sealed class CompositionVocabularyTests {
    [Fact]
    public void WorldMeansCompositionAtTheRootAndStateRowsInsideState() {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(construct: out var composition, enclosing: null, keyword: "world"));
        Assert.Equal(expected: WorldRootArm.CompileTime, actual: composition!.RootArm);
        Assert.Equal(expected: "world name = module(arguments)", actual: composition.Grammar);

        Assert.True(condition: table.TryGet(construct: out var rows, enclosing: "state", keyword: "world"));
        Assert.Null(@object: rows!.RootArm);
        Assert.Equal(expected: "state.world", actual: rows.DocumentMember);
    }
    // A construct whose expansion writes generated names is read back from them; one whose expansion writes only
    // ordinary document fields prints as those fields.
    [InlineData("world", true)]
    [InlineData("border", true)]
    [InlineData("door", true)]
    [InlineData("asset", false)]
    [InlineData("ground", true)]
    [InlineData("spawn", false)]
    [Theory]
    public void CompositionConstructsProjectThroughTheCompileTimeArm(string keyword, bool readBack) {
        Assert.True(condition: WorldConstructs.Table.TryGet(construct: out var construct, enclosing: null, keyword: keyword));
        Assert.Equal(expected: "(nothing)", actual: construct!.DocumentMember);
        Assert.Equal(expected: WorldRootArm.CompileTime, actual: construct.RootArm);
        Assert.Equal(expected: readBack, actual: construct.Sugar.Printed);
        Assert.Equal(expected: readBack, actual: construct.Sugar.FromRows);
    }
    [Fact]
    public void EndpointAndLinkMembersDescribeEverySupportedProperty() {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(construct: out var border, enclosing: null, keyword: "border"));
        Assert.Equal(
            expected: ["center", "height", "hysteresis", "left", "pitch", "right", "width", "yaw"],
            actual: border!.Members.Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal)
        );

        Assert.True(condition: table.TryGet(construct: out var ground, enclosing: null, keyword: "ground"));
        Assert.Equal(expected: ["center", "name", "size"], actual: ground!.Members.Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal));

        Assert.True(condition: table.TryGet(construct: out var spawn, enclosing: null, keyword: "spawn"));
        Assert.Equal(expected: ["at", "name", "yaw"], actual: spawn!.Members.Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal));
    }
}
