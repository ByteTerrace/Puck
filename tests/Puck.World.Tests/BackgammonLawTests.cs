using Xunit;

namespace Puck.World.Tests;

/// <summary>Backgammon is the market's chance-node probe: a ring of 24 points, checkers on a keyed row, dice as a
/// two-cell draw row the AI's search averages over at a chance ply, and a judge rule that refuses a checker's own
/// move while a different one of its side's checkers still occupies the bar.</summary>
public sealed class BackgammonLawTests {
    private static long Cell(WorldFixture fixture, string row, string key) => Row(
        fixture: fixture,
        name: row
    ).Cells!.Single(predicate: c => (c.Key.Value == key)).Value.Raw;
    private static WorldDefinition Load() {
        var path = Path.Combine(
            RepoRoot(),
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "games",
            "backgammon.world.json"
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path,
                out var definition,
                out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition!,
                reason: out var invalid
            ),
            userMessage: invalid
        );

        return definition!;
    }
    // Authors the starting position directly (never pokes a live cell after boot): a raw write to checkerPoint or
    // dice would itself cross the same accept rules the search's judge shares, flipping turn for real before the
    // search ever runs — exactly the trap this fixture avoids.
    private static WorldDefinition Position(long w0, long w1, long d1, long d2) {
        var loaded = Load();
        var state = loaded.StateRaw!;
        var rows = new List<WorldStateRow>(collection: (state.World ?? []));

        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index].Name.Value == "checkerPoint") {
                rows[index] = rows[index] with { Cells = [new StateCell(
                        CellName.Parse(candidate: "w0"),
                        CellValue.Int(value: w0)
                    ), new StateCell(
                        CellName.Parse(candidate: "w1"),
                        CellValue.Int(value: w1)
                    ), new StateCell(
                        CellName.Parse(candidate: "b0"),
                        CellValue.Int(value: 3)
                    ), new StateCell(
                        CellName.Parse(candidate: "b1"),
                        CellValue.Int(value: 1)
                    )] };
            } else if (rows[index].Name.Value == "origPoint") {
                rows[index] = rows[index] with { Cells = [new StateCell(
                        CellName.Parse(candidate: "w0"),
                        CellValue.Int(value: w0)
                    ), new StateCell(
                        CellName.Parse(candidate: "w1"),
                        CellValue.Int(value: w1)
                    ), new StateCell(
                        CellName.Parse(candidate: "b0"),
                        CellValue.Int(value: 3)
                    ), new StateCell(
                        CellName.Parse(candidate: "b1"),
                        CellValue.Int(value: 1)
                    )] };
            } else if (rows[index].Name.Value == "dice") {
                rows[index] = rows[index] with { Cells = [new StateCell(
                        CellName.Parse(candidate: "d1"),
                        CellValue.Int(value: d1)
                    ), new StateCell(
                        CellName.Parse(candidate: "d2"),
                        CellValue.Int(value: d2)
                    )] };
            }
        }

        var definition = loaded with { StateRaw = state with { World = rows } };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var invalid
            ),
            userMessage: invalid
        );

        return definition;
    }
    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(
        rows: fixture.Server.Definition.State,
        name: name
    )!;
    private static ArenaSearchStatus Settle(WorldFixture fixture, int maxTicks = 4000) {
        var status = fixture.Server.SearchStatus()[0];

        for (var tick = 0; ((tick < maxTicks) && !status.Done); tick++) {
            fixture.Step();
            status = fixture.Server.SearchStatus()[0];
        }

        return status;
    }

    // White's checker w0 sits on the bar (checkerPoint -1); w1 stands on the board at 22. With dice 1 and 5, w1's own
    // ordinary relocate to 23 (a die of 1) or w0's own entry (a die of 1 lands at point 0, a die of 5 at point 4) are
    // the candidates the walk resolves — the judge accepts w0's entry (the control: a body with no other constraint
    // may always clear its own bar) and refuses every one of w1's candidates outright, because a different one of
    // white's own checkers still occupies the bar.
    [Fact]
    public void ABodyWithACheckerOnTheBarNeverMovesAnyOtherChecker() {
        using var fixture = Fixtures.FreshServer(definition: Position(
            d1: 1,
            d2: 5,
            w0: -1,
            w1: 22
        ));

        var status = Settle(fixture);

        Assert.True(
            condition: status.Done,
            userMessage: status.ToString()
        );
        // The control: white's bar checker itself may still enter — a denial that can never fire proves nothing.
        Assert.True(
            condition: (Cell(
                fixture: fixture,
                key: "w0",
                row: "moveCounts"
            ) > 0L),
            userMessage: "w0's own bar entry must stay legal"
        );
        // The denial the law is about: w1 owns no accepted candidate while w0 sits on the bar.
        Assert.Equal(
            0L,
            Cell(
                fixture: fixture,
                key: "w1",
                row: "moveCounts"
            )
        );
    }
    [Fact]
    public void TheDocumentValidatesAndBootsWithASingleChanceBearingSearchJob() {
        var definition = Load();

        Assert.Single(collection: definition.Search.Rows);
        Assert.NotNull(@object: definition.Search.Rows[0].Chance);
        Assert.Equal(
            "dice",
            definition.Search.Rows[0].Chance!.Row
        );
    }
    // The same position with w0 already home (no bar occupant) lets w1 move normally: the discriminating half of the
    // law above — the search's own judge, not an authoring accident, is what is under test.
    [Fact]
    public void WithNoCheckerOnTheBarTheOtherCheckerMovesNormally() {
        using var fixture = Fixtures.FreshServer(definition: Position(
            d1: 1,
            d2: 5,
            w0: 20,
            w1: 22
        ));

        var status = Settle(fixture);

        Assert.True(
            condition: status.Done,
            userMessage: status.ToString()
        );
        Assert.True(
            condition: (Cell(
                fixture: fixture,
                key: "w1",
                row: "moveCounts"
            ) > 0L),
            userMessage: "w1 must be free to move its own ordinary distance when the bar is clear"
        );
    }
}
