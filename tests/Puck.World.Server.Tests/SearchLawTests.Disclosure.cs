using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class SearchLawTests {
    [Fact]
    public void SearchIncludesJudgeIterationMembershipInItsAudience() {
        var definition = MiniNegamaxWorld(1);
        definition = definition.WithWorldState([.. definition.AuthoredState,
            new WorldStateRow(CellName.Parse("secretKeys"), CellKind.Int,
                Cells: [new StateCell(CellName.Parse("one"), CellValue.Int(1))],
                Visibility: new StateVisibility(Readers: ["seat1"]))]) with {
            Rules = [definition.Rules![0] with { ForEach = "secretKeys" }],
        };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains("SearchWidensAudience", reason);
        Assert.Contains("secretKeys", reason);
    }

    [Theory]
    [InlineData("pieceCell", false)]
    [InlineData("pieceCell", true)]
    [InlineData("turn", false)]
    [InlineData("verdict", false)]
    public void SearchRefusesAnOutputWiderThanItsDeclaredInputs(string input, bool cellPolicy) {
        var definition = MiniNegamaxWorld(1);
        var restricted = new StateVisibility(Readers: ["seat1"]);
        definition = definition.WithWorldState(definition.AuthoredState.Select(row => row.Name.Value != input ? row :
            cellPolicy ? row with { Visibility = new StateVisibility(), Cells = row.Cells!.Select(cell => cell with { Visibility = restricted }).ToArray() } :
            row with { Visibility = restricted }).ToArray());
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains("SearchWidensAudience", reason);
        Assert.Contains(input, reason);

        definition = definition.WithWorldState(definition.AuthoredState.Select(row => row.Name.Value == "best" ?
            row with { Visibility = restricted } : row).ToArray());
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out reason), reason);
    }

    [Fact]
    public void SearchRefusesLiveAudienceNarrowingAndRestartsWhenRestored() {
        var definition = MiniNegamaxWorld(2, nodes: 1);
        // Reserve the optional live visibility column with a public declaration.
        definition = definition.WithWorldState(definition.AuthoredState.Select(row => row.Name.Value == "pieceCell" ?
            row with { Visibility = new StateVisibility() } : row).ToArray());
        using var fixture = Fixtures.FreshServer(definition);
        var server = fixture.Server;
        for (var tick = 0; tick < 3; tick++) { fixture.Step(); }
        Assert.True(server.SearchStatus()[0].Running);
        var before = fixture.Row("best").Cells!.Select(cell => cell.Value).ToArray();
        Assert.True(server.Arena.Catalog.TryResolve(StateLane.Document, "pieceCell", out var row));
        var key = server.Arena.Keys.Intern(CellName.Parse("a"));
        Assert.True(server.Arena.TryWriteVisibility(row.Ordinal, key, new StateVisibility(Readers: ["seat1"])));
        for (var tick = 0; tick < 30; tick++) { fixture.Step(); }
        Assert.Equal(before, fixture.Row("best").Cells!.Select(cell => cell.Value).ToArray());
        Assert.False(server.SearchStatus()[0].Running);
        Assert.False(server.SearchStatus()[0].Done);
        Assert.Equal(1, server.SearchStatus()[0].Faults);
        Assert.True(server.Arena.TryWriteVisibility(row.Ordinal, key, null));
        for (var tick = 0; tick < 500 && !server.SearchStatus()[0].Done; tick++) { fixture.Step(); }
        Assert.True(server.SearchStatus()[0].Done);
    }
}
