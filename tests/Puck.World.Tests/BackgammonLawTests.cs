using Xunit;

namespace Puck.World.Tests;

/// <summary>Backgammon is the market's chance-node probe: a ring of 24 points, checkers on a keyed row, dice as a
/// two-cell draw row the AI's search averages over at a chance ply, and a judge rule that refuses a checker's own
/// move while a different one of its side's checkers still occupies the bar.</summary>
public sealed class BackgammonLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static WorldDefinition Load() {
        var path = Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "games", "backgammon.world.json");

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason), reason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition!, out var invalid), invalid);

        return definition!;
    }

    // Authors the starting position directly (never pokes a live cell after boot): a raw write to checkerPoint or
    // dice would itself cross the same accept rules the search's judge shares, flipping turn for real before the
    // search ever runs — exactly the trap this fixture avoids.
    private static WorldDefinition Position(long w0, long w1, long d1, long d2) {
        var loaded = Load();
        var state = loaded.StateRaw!;
        var rows = new List<WorldStateRow>(state.World ?? []);

        for (var index = 0; index < rows.Count; index++) {
            if (rows[index].Name.Value == "checkerPoint") {
                rows[index] = rows[index] with { Cells = [new StateCell(CellName.Parse("w0"), w0), new StateCell(CellName.Parse("w1"), w1), new StateCell(CellName.Parse("b0"), 3), new StateCell(CellName.Parse("b1"), 1)] };
            } else if (rows[index].Name.Value == "origPoint") {
                rows[index] = rows[index] with { Cells = [new StateCell(CellName.Parse("w0"), w0), new StateCell(CellName.Parse("w1"), w1), new StateCell(CellName.Parse("b0"), 3), new StateCell(CellName.Parse("b1"), 1)] };
            } else if (rows[index].Name.Value == "dice") {
                rows[index] = rows[index] with { Cells = [new StateCell(CellName.Parse("d1"), d1), new StateCell(CellName.Parse("d2"), d2)] };
            }
        }

        var definition = loaded with { StateRaw = state with { World = rows } };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    private static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name)!;
    private static long Cell(WorldFixture fixture, string row, string key) => Row(fixture, row).Cells!.Single(c => c.Key.Value == key).Value;

    private static SearchStatus Settle(WorldFixture fixture, int maxTicks = 4000) {
        var status = fixture.Server.SearchStatus()[0];

        for (var tick = 0; (tick < maxTicks) && !status.Done; tick++) {
            fixture.Step();
            status = fixture.Server.SearchStatus()[0];
        }

        return status;
    }

    [Fact]
    public void TheDocumentValidatesAndBootsWithASingleChanceBearingSearchJob() {
        var definition = Load();

        Assert.Single(definition.Search.Rows);
        Assert.NotNull(definition.Search.Rows[0].Chance);
        Assert.Equal("dice", definition.Search.Rows[0].Chance!.Row);
    }

    // White's checker w0 sits on the bar (checkerPoint -1); w1 stands on the board at 22. With dice 1 and 5, w1's own
    // ordinary relocate to 23 (a die of 1) or w0's own entry (a die of 1 lands at point 0, a die of 5 at point 4) are
    // the candidates the walk resolves — the judge accepts w0's entry (the control: a body with no other constraint
    // may always clear its own bar) and refuses every one of w1's candidates outright, because a different one of
    // white's own checkers still occupies the bar.
    [Fact]
    public void ABodyWithACheckerOnTheBarNeverMovesAnyOtherChecker() {
        using var fixture = Fixtures.FreshServer(definition: Position(w0: -1, w1: 22, d1: 1, d2: 5));

        var status = Settle(fixture);

        Assert.True(status.Done, status.ToString());
        // The control: white's bar checker itself may still enter — a denial that can never fire proves nothing.
        Assert.True(Cell(fixture, "moveCounts", "w0") > 0L, "w0's own bar entry must stay legal");
        // The denial the law is about: w1 owns no accepted candidate while w0 sits on the bar.
        Assert.Equal(0L, Cell(fixture, "moveCounts", "w1"));
    }

    // The same position with w0 already home (no bar occupant) lets w1 move normally: the discriminating half of the
    // law above — the search's own judge, not an authoring accident, is what is under test.
    [Fact]
    public void WithNoCheckerOnTheBarTheOtherCheckerMovesNormally() {
        using var fixture = Fixtures.FreshServer(definition: Position(w0: 20, w1: 22, d1: 1, d2: 5));

        var status = Settle(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.True(Cell(fixture, "moveCounts", "w1") > 0L, "w1 must be free to move its own ordinary distance when the bar is clear");
    }
}
