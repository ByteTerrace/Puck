using Xunit;

namespace Puck.World.Tests;

/// <summary>Checks the shipped board programs against coordinate arithmetic, independent of their bitboards,
/// direction names, shifts, and attack queries. The physical chess import is covered by ChessModuleImportLawTests.</summary>
public sealed class AuthoredBoardRulesLawTests {
    private static readonly Lazy<WorldDefinition> GardenSource = new(() => Load("src/Puck.World/Assets/worlds/puck.world.json"));
    private static WorldDefinition Garden => GardenSource.Value;
    private static readonly WorldDefinition ChessModule = Load("tests/Puck.World.Tests/Fixtures/minimal-chess-host.world.json");

    private static WorldDefinition Load(string relativePath) {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(WorldDefinitionFileSource.TryLoad(path, out var definition, out _, out var reason), reason);
        return definition!;
    }

    private static WorldStateRow Seed(WorldStateRow row, params long[] values) => row with {
        Cells = [.. values.Select((value, index) => new StateCell(
            CellName.Parse(values.Length == 1 ? WorldStateRow.SlotKey : index.ToString()), value))],
    };

    private static WorldDefinition Chess(long[] board) {
        var rules = ChessModule.Rules!.Where(r => r.Name.Value is
            "tabletop-check-white" or "tabletop-check-black").ToArray();
        var names = new HashSet<string> { "board", "settleHold", "inCheck" };
        foreach (var effect in rules.SelectMany(r => r.Effects).OfType<ActionEffect.SetState>()) {
            names.Add(effect.State);
        }
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [.. ChessModule.State.Where(r => names.Contains(r.Name.Value)).Select(r =>
                r.Name.Value switch { "board" => Seed(r, board), "settleHold" => Seed(r, 60), _ => r })],
                Lattices: [ChessModule.StateRaw!.Lattices!.Single(t => t.Name == "chessBoard")]),
            Rules = rules,
        };
    }

    private static bool Attacked(long[] board, int king, int attackerSign) {
        if (king < 0) { return false; }
        for (var cell = 0; cell < 64; cell++) {
            if (Math.Sign(board[cell]) != attackerSign) { continue; }
            var dx = cell % 8 - king % 8;
            var dy = cell / 8 - king / 8;
            var x = Math.Abs(dx);
            var y = Math.Abs(dy);
            var piece = Math.Abs(board[cell]);
            if (piece == 1 && x == 1 && dy == -attackerSign) { return true; }
            if (piece == 2 && x * y == 2) { return true; }
            if (piece == 6 && Math.Max(x, y) == 1) { return true; }
            var diagonal = x == y && x != 0;
            var straight = (x == 0) != (y == 0);
            if (!((piece is 3 or 5 && diagonal) || (piece is 4 or 5 && straight))) { continue; }
            var step = Math.Sign(dx) + 8 * Math.Sign(dy);
            var blocked = false;
            for (var probe = king + step; probe != cell; probe += step) {
                if (board[probe] != 0) { blocked = true; break; }
            }
            if (!blocked) { return true; }
        }
        return false;
    }

    private static void CheckChess(long[] board) {
        using var fixture = Fixtures.FreshServer(definition: Chess(board));
        fixture.Step();
        var actual = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "inCheck")!;
        for (var side = 0; side < 2; side++) {
            var king = Array.IndexOf(board, side == 0 ? 6L : -6L);
            Assert.Equal(Attacked(board, king, side == 0 ? -1 : 1) ? 1L : 0L,
                actual.Cells!.Single(c => c.Key.Value == side.ToString()).Value);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ChessAttacksRespectEveryOriginIncludingEdgesAndTheSignBit(int sign) {
        int[] offsets = [9, 17, 18, 16, 24, 1];
        for (var king = 0; king < 64; king++) {
            for (var piece = 1; piece <= 6; piece++) {
                var board = new long[64];
                board[king] = -sign * 6;
                // Deliberately wrap the numeric index: the coordinate oracle rejects false edge neighbours.
                board[(king - sign * offsets[piece - 1] + 64) % 64] = sign * piece;
                CheckChess(board);
            }
        }
    }

    [Fact]
    public void ChessAttacksAgreeWithCoordinatesForEveryPairOfSquares() {
        var board = new long[64];
        var definition = Chess(board);
        var rows = definition.State;
        var layout = new FrameLayout(rows, name => WorldTopologyCompilation.Find(definition, name));
        var host = new FrameHost(layout, rows, definition.StateCatalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));
        var rules = WorldRuleCompiler.CompileAll(definition);
        var occupancy = WorldDefinitionRows.FindStateRow(rows, "board")!;
        var check = WorldDefinitionRows.FindStateRow(rows, "inCheck")!;
        void Put(int square, long code) {
            board[square] = code;
            Assert.True(host.Frame.TryWrite(occupancy, CellName.Parse(square.ToString()), code, StateWriteKind.Set, out var reason), reason);
        }
        // Compile the shipped judge once, then exhaust every origin/target/piece/colour
        // through its real frame evaluator. The expected answer uses coordinate rays.
        for (var side = 0; side < 2; side++) {
            var sign = side == 0 ? -1 : 1;
            for (var king = 0; king < 64; king++) {
                Put(king, -sign * 6);
                for (var piece = 1; piece <= 6; piece++) {
                    for (var target = 0; target < 64; target++) {
                        if (target == king) { continue; }
                        Put(target, sign * piece);
                        host.Judge(rules, 1);
                        Assert.True(host.Frame.TryStored(check, CellName.Parse(side.ToString()), out var actual, out _));
                        Assert.Equal(Attacked(board, king, sign) ? 1L : 0L, actual);
                        Put(target, 0);
                    }
                }
                Put(king, 0);
            }
        }
        Assert.Equal(0, host.Refusals);
    }

    [Fact]
    public void ChessSlidersStopAtTheFirstPieceAndMissingKingsAreNotInCheck() {
        CheckChess(new long[64]);
        foreach (var sign in new[] { -1, 1 }) {
            foreach (var (dx, dy) in new[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) }) {
                foreach (var blocker in new[] { 0, -sign, sign * 2, sign * 5 }) {
                    var board = new long[64];
                    board[27] = -sign * 6;
                    board[27 + 3 * (dx + 8 * dy)] = sign * (dx != 0 && dy != 0 ? 3 : 4);
                    board[27 + dx + 8 * dy] = blocker;
                    CheckChess(board);
                }
            }
        }
    }

    private static WorldDefinition CastlePosition(long[] before, long[] after, string ruleName, int moveKind = 4) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [.. ChessModule.State.Where(r => r.Name.Value is
            "board" or "lastLegal" or "move" or "settleHold" or "castleRights" or "castleTransitAttacked").Select(r => r.Name.Value switch {
                "board" => Seed(r, after), "lastLegal" => Seed(r, before), "settleHold" => Seed(r, 60),
                "move" => r with { Cells = [.. r.Cells!.Select(c => c with { Value = c.Key.Value == "kind" ? moveKind : -1 })] },
                _ => r,
            })], Lattices: [ChessModule.StateRaw!.Lattices!.Single(t => t.Name == "chessBoard")]),
        Rules = [ChessModule.Rules!.Single(r => r.Name.Value == ruleName)],
    };

    [Theory]
    [InlineData("wk", 5, -1)]
    [InlineData("wq", 3, -1)]
    [InlineData("bk", 61, 1)]
    [InlineData("bq", 59, 1)]
    public void CastleTransitAttacksReadPiecesAtEverySquare(string side, int transit, int sign) {
        void Check(long[] board) {
            using var fixture = Fixtures.FreshServer(definition: CastlePosition(board, board, "tabletop-castle-transit-attacked"));
            fixture.Step();
            var result = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "castleTransitAttacked")!;
            Assert.Equal(Attacked(board, transit, sign) ? 1L : 0L, result.Cells!.Single(c => c.Key.Value == side).Value);
        }
        Check(new long[64]);
        for (var piece = 1; piece <= 6; piece++) {
            for (var cell = 0; cell < 64; cell++) {
                if (cell == transit) { continue; }
                var board = new long[64];
                board[cell] = sign * piece;
                Check(board);
            }
        }
        // A friendly blocker interrupts the same rook ray that otherwise attacks the transit square.
        var blocked = new long[64];
        var step = sign < 0 ? 8 : -8;
        blocked[transit + 3 * step] = sign * 4;
        Check(blocked);
        blocked[transit + step] = -sign;
        Check(blocked);
    }

    private static IEnumerable<int[]> Lines() {
        for (var start = 0; start < 64; start++) {
            for (var dx = -1; dx <= 1; dx++) {
                for (var dy = -1; dy <= 1; dy++) {
                    for (var dz = -1; dz <= 1; dz++) {
                        var step = dx + 4 * dy + 16 * dz;
                        if (step <= 0) { continue; }
                        var x = start % 4 + 3 * dx;
                        var y = start / 4 % 4 + 3 * dy;
                        var z = start / 16 + 3 * dz;
                        if (x is >= 0 and < 4 && y is >= 0 and < 4 && z is >= 0 and < 4) {
                            yield return [start, start + step, start + 2 * step, start + 3 * step];
                        }
                    }
                }
            }
        }
    }

    private static long QubicWinner(long[] board) {
        var source = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [.. Garden.State.Where(r => r.Name.Value.StartsWith("ttt", StringComparison.Ordinal)).Select(r =>
                r.Name.Value switch {
                    "tttBoard" => Seed(r, board), "tttBoardVersion" => Seed(r, 1),
                    "tttMoveCount" => Seed(r, board.Count(v => v != 0)), _ => r,
                })], Lattices: [Garden.StateRaw!.Lattices!.Single(t => t.Name == "tttCube")]),
            Rules = [Garden.Rules!.Single(r => r.Name.Value == "ttt-check-win")],
        };
        using var fixture = Fixtures.FreshServer(definition: source);
        fixture.Step();
        return WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "tttWinner")!.Cells!.Single().Value;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void QubicRecognizesAll76LinesAndRejectsEveryThreeMarkNearMiss(int player) {
        var lines = Lines().ToArray();
        Assert.Equal(76, lines.Length);
        foreach (var line in lines) {
            var board = new long[64];
            foreach (var cell in line) { board[cell] = player; }
            Assert.Equal(player, QubicWinner(board));
            foreach (var missing in line) {
                board[missing] = 0;
                Assert.Equal(0, QubicWinner(board));
                board[missing] = player;
            }
        }
        // Four consecutive integers across a row boundary are not a geometric line.
        var wrap = new long[64];
        foreach (var cell in new[] { 2, 3, 4, 5 }) { wrap[cell] = player; }
        Assert.Equal(0, QubicWinner(wrap));
    }
}
