using Xunit;

namespace Puck.World.Tests;

/// <summary>The authored 4x4x4 tic-tac-toe rule recognizes exactly the cube's 76 winning lines.</summary>
public sealed class QubicLawTests {
    private static readonly WorldDefinition Garden = AuthoredGameFixtures.Program(module: "tictactoe");

    private static IEnumerable<int[]> Lines() {
        for (var start = 0; (start < 64); start++) {
            for (var dx = -1; (dx <= 1); dx++) {
                for (var dy = -1; (dy <= 1); dy++) {
                    for (var dz = -1; (dz <= 1); dz++) {
                        var step = ((dx + (4 * dy)) + (16 * dz));

                        if (step <= 0) { continue; }
                        var x = ((start % 4) + (3 * dx));
                        var y = (((start / 4) % 4) + (3 * dy));
                        var z = ((start / 16) + (3 * dz));

                        if ((x is >= 0 and < 4) && (y is >= 0 and < 4) && (z is >= 0 and < 4)) {
                            yield return [start, (start + step), (start + (2 * step)), (start + (3 * step))];
                        }
                    }
                }
            }
        }
    }
    private static WorldStateRow Seed(WorldStateRow row, params long[] values) => row with {
        Cells = [.. values.Select(selector: (value, index) => new StateCell(
            CellName.Parse(candidate: ((values.Length == 1) ? WorldStateRow.SlotKey : index.ToString())),
            CellValue.Int(value: value)
        ))],
    };
    private long Winner(long[] board) {
        var source = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                World: [.. Garden.State.Where(predicate: row => row.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: "ttt")).Select(selector: row => row.Name.Value switch {
                    "tttBoard" => Seed(row, board),
                    "tttBoardVersion" => Seed(row, 1),
                    "tttMoveCount" => Seed(row, board.Count(predicate: value => (value != 0))),
                    _ => row,
                })],
                Lattices: [Garden.StateRaw!.Lattices!.Single(predicate: topology => (topology.Name == "tttCube"))]
            ),
            Rules = [Garden.Rules!.Single(predicate: rule => (rule.Name.Value == "ttt-check-win"))],
        };
        var fixture = m_judge ??= new RuleArenaFixture(definition: source);

        fixture.Evaluate(position: source);
        return fixture.Read("tttWinner");
    }

    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void EveryLineWinsAndEveryThreeMarkNearMissDoesNot(int player) {
        var lines = Lines().ToArray();

        Assert.Equal(76, lines.Length);
        foreach (var line in lines) {
            var board = new long[64];

            foreach (var cell in line) { board[cell] = player; }
            Assert.Equal(player, Winner(board: board));
            foreach (var missing in line) {
                board[missing] = 0;
                Assert.Equal(0, Winner(board: board));
                board[missing] = player;
            }
        }

        var wrap = new long[64];

        foreach (var cell in new[] { 2, 3, 4, 5 }) { wrap[cell] = player; }
        Assert.Equal(0, Winner(board: wrap));
    }

    private RuleArenaFixture? m_judge;
}
