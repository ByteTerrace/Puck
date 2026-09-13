using Xunit;

namespace Puck.World.Tests;

/// <summary>Checks the shipped board programs against coordinate arithmetic, independent of their bitboards,
/// direction names, shifts, and attack queries. The physical chess import is covered by ChessModuleImportLawTests.</summary>
public sealed class AuthoredBoardRulesLawTests {
    private static readonly WorldDefinition Garden = AuthoredGameFixtures.Program(module: "tictactoe");
    private static readonly WorldDefinition ChessModule = Load(relativePath: "tests/Puck.World.Tests/Fixtures/minimal-chess-host.world.json");

    private static bool Attacked(long[] board, int king, int attackerSign) {
        if (king < 0) { return false; }
        for (var cell = 0; (cell < 64); cell++) {
            if (Math.Sign(value: board[cell]) != attackerSign) { continue; }
            var dx = ((cell % 8) - (king % 8));
            var dy = ((cell / 8) - (king / 8));
            var x = Math.Abs(value: dx);
            var y = Math.Abs(value: dy);
            var piece = Math.Abs(value: board[cell]);

            if (
                (piece == 1) &&
                (x == 1) &&
                (dy == -attackerSign)
            ) { return true; }
            if (
                (piece == 2) &&
                ((x * y) == 2)
            ) { return true; }
            if (
                (piece == 6) &&
                (Math.Max(
                val1: x,
                val2: y
            ) == 1)
            ) { return true; }
            var diagonal = ((x == y) && (x != 0));
            var straight = ((x == 0) != (y == 0));

            if (!(((piece is 3 or 5) && diagonal) || ((piece is 4 or 5) && straight))) { continue; }
            var step = (Math.Sign(value: dx) + (8 * Math.Sign(value: dy)));
            var blocked = false;

            for (var probe = (king + step); (probe != cell); probe += step) {
                if (board[probe] != 0) { blocked = true; break; }
            }
            if (!blocked) { return true; }
        }
        return false;
    }
    private static WorldDefinition CastlePosition(long[] before, long[] after, string ruleName, int moveKind = 4) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
        World: [.. ChessModule.State.Where(predicate: r => (r.Name.Value is
            "board" or "pieceCell" or "pieceCode" or "lastLegal" or "move" or "settleHold" or "castleRights" or "castleTransitAttacked")).Select(selector: r => r.Name.Value switch {
                "pieceCell" => r with { Cells = Tokens(board: after).Cells }, "pieceCode" => r with { Cells = Tokens(board: after).Codes },
                "lastLegal" => Seed(
                r,
                before
            ), "settleHold" => Seed(
                r,
                60
            ),
                "move" => r with { Cells = [.. r.Cells!.Select(selector: c => c with { Value = ((c.Key.Value == "kind")
        ? moveKind
        : -1) })] },
                _ => r,
            })],
        Lattices: [ChessModule.StateRaw!.Lattices!.Single(predicate: t => (t.Name == "chessBoard"))]
    ),
        Rules = [ChessModule.Rules!.Single(predicate: r => (r.Name.Value == ruleName))],
    };
    private void CheckChess(long[] board) {
        var position = Chess(board: board);
        var fixture = m_chessJudge ??= new RuleFrameFixture(definition: position);

        fixture.Evaluate(position: position);
        for (var side = 0; (side < 2); side++) {
            var king = Array.IndexOf(
                array: board,
                value: ((side == 0)
                ? 6L
                : -6L)
            );

            Assert.Equal(
                (Attacked(
                    attackerSign: ((side == 0)
                ? -1
                : 1),
                    board: board,
                    king: king
                )
                ? 1L
                : 0L),
                fixture.Read(
                    "inCheck",
                    side.ToString()
                )
            );
        }
    }
    private static WorldDefinition Chess(long[] board) {
        var (tokenCells, tokenCodes) = Tokens(board: board);
        var rules = ChessModule.Rules!.Where(predicate: r => (r.Name.Value is
            "tabletop-check-white" or "tabletop-check-black")).ToArray();
        var names = new HashSet<string> { "board", "pieceCell", "pieceCode", "settleHold", "inCheck" };

        foreach (var effect in rules.SelectMany(selector: r => r.Effects).OfType<ActionEffect.SetState>()) {
            names.Add(item: effect.State);
        }
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
            World: [.. ChessModule.State.Where(predicate: r => names.Contains(item: r.Name.Value)).Select(selector: r =>
                r.Name.Value switch { "pieceCell" => r with { Cells = tokenCells }, "pieceCode" => r with { Cells = tokenCodes }, "settleHold" => Seed(
                    r,
                    60
                ), _ => r })],
            Lattices: [ChessModule.StateRaw!.Lattices!.Single(predicate: t => (t.Name == "chessBoard"))]
        ),
            Rules = rules,
        };
    }
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

                        if (
                            (x is >= 0 and < 4) &&
                            (y is >= 0 and < 4) &&
                            (z is >= 0 and < 4)
                        ) {
                            yield return [start, (start + step), (start + (2 * step)), (start + (3 * step))];
                        }
                    }
                }
            }
        }
    }
    private static WorldDefinition Load(string relativePath) {
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
        var path = Path.Combine(
            path1: directory.FullName,
            path2: relativePath
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path,
                out var definition,
                out _,
                out var reason
            ),
            userMessage: reason
        );
        return definition!;
    }
    private long QubicWinner(long[] board) {
        var source = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
            World: [.. Garden.State.Where(predicate: r => r.Name.Value.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "ttt"
                )).Select(selector: r =>
                r.Name.Value switch {
                    "tttBoard" => Seed(
                    r,
                    board
                ), "tttBoardVersion" => Seed(
                    r,
                    1
                ),
                    "tttMoveCount" => Seed(
                    r,
                    board.Count(predicate: v => (v != 0))
                ), _ => r,
                })],
            Lattices: [Garden.StateRaw!.Lattices!.Single(predicate: t => (t.Name == "tttCube"))]
        ),
            Rules = [Garden.Rules!.Single(predicate: r => (r.Name.Value == "ttt-check-win"))],
        };
        var fixture = m_qubicJudge ??= new RuleFrameFixture(definition: source);

        fixture.Evaluate(position: source);
        return fixture.Read("tttWinner");
    }
    private static WorldStateRow Seed(WorldStateRow row, params long[] values) => row with {
        Cells = [.. values.Select(selector: (value, index) => new StateCell(
            CellName.Parse(candidate: ((values.Length == 1)
        ? WorldStateRow.SlotKey
        : index.ToString())),
            value
        ))],
    };
    // The board is derived from its token rows (inverse), so a position is seeded as tokens: one token per occupied
    // cell in cell order, the rest off the board.
    private static (StateCell[] Cells, StateCell[] Codes) Tokens(long[] board) {
        var cells = new StateCell[32];
        var codes = new StateCell[32];
        var next = 0;

        for (var cell = 0; (cell < board.Length); cell++) {
            if (board[cell] != 0) {
                cells[next] = new StateCell(
                    CellName.Parse(candidate: $"piece{next}"),
                    cell
                );
                codes[next] = new StateCell(
                    CellName.Parse(candidate: $"piece{next}"),
                    board[cell]
                );
                next++;
            }
        }
        for (; (next < 32); next++) {
            cells[next] = new StateCell(
                CellName.Parse(candidate: $"piece{next}"),
                -1
            );
            codes[next] = new StateCell(
                CellName.Parse(candidate: $"piece{next}"),
                0
            );
        }
        return (cells, codes);
    }

    [InlineData("wk", 5, -1)]
    [InlineData("wq", 3, -1)]
    [InlineData("bk", 61, 1)]
    [InlineData("bq", 59, 1)]
    [Theory]
    public void CastleTransitAttacksReadPiecesAtEverySquare(string side, int transit, int sign) {
        var empty = new long[64];
        var fixture = new RuleFrameFixture(definition: CastlePosition(
            empty,
            empty,
            "tabletop-castle-transit-attacked"
        ));

        void Check(long[] board) {
            fixture.Evaluate(position: CastlePosition(
                board,
                board,
                "tabletop-castle-transit-attacked"
            ));
            Assert.Equal(
                (Attacked(
                    attackerSign: sign,
                    board: board,
                    king: transit
                )
                ? 1L
                : 0L),
                fixture.Read(
                    key: side,
                    row: "castleTransitAttacked"
                )
            );
        }
        Check(board: new long[64]);
        for (var piece = 1; (piece <= 6); piece++) {
            for (var cell = 0; (cell < 64); cell++) {
                if (cell == transit) { continue; }
                var board = new long[64];

                board[cell] = (sign * piece);
                Check(board: board);
            }
        }
        // A friendly blocker interrupts the same rook ray that otherwise attacks the transit square.
        var blocked = new long[64];
        var step = ((sign < 0)
            ? 8
            : -8
        );

        blocked[(transit + (3 * step))] = (sign * 4);
        Check(board: blocked);
        blocked[(transit + step)] = -sign;
        Check(board: blocked);
    }
    [Fact]
    public void ChessAttacksAgreeWithCoordinatesForEveryPairOfSquares() {
        var board = new long[64];
        var definition = Chess(board: board);
        var rows = definition.State;
        var layout = new FrameLayout(
            rows: rows,
            topology: name => WorldTopologyCompilation.Find(
                definition: definition,
                name: name
            )
        );
        var host = new FrameHost(
            layout,
            rows,
            definition.StateCatalog,
            CompiledPatterns.Empty,
            []
        );

        host.Frame.Load(source: new RowStore(rows: rows));
        var rules = WorldRuleCompiler.CompileAll(definition: definition);
        var tokenRow = WorldDefinitionRows.FindStateRow(
            name: "pieceCell",
            rows: rows
        )!;
        var codeRow = WorldDefinitionRows.FindStateRow(
            name: "pieceCode",
            rows: rows
        )!;
        var check = WorldDefinitionRows.FindStateRow(
            name: "inCheck",
            rows: rows
        )!;
        // The board derives from its tokens, so a square is set by moving a token onto it (or off the board for 0):
        // the token already standing there, else the first token off the board.
        void Put(int square, long code) {
            board[square] = code;
            var token = -1;

            for (var candidate = 0; ((candidate < 32) && (token < 0)); candidate++) {
                if (
                    host.Frame.TryStoredAt(
                    index: candidate,
                    row: tokenRow,
                    value: out var standing
                ) &&
                    (standing == square)
                ) { token = candidate; }
            }
            for (var candidate = 0; ((candidate < 32) && (token < 0)); candidate++) {
                if (
                    host.Frame.TryStoredAt(
                    index: candidate,
                    row: tokenRow,
                    value: out var standing
                ) &&
                    (standing < 0)
                ) { token = candidate; }
            }
            Assert.True(condition: (token >= 0));
            var key = CellName.Parse(candidate: $"piece{token}");

            Assert.True(
                condition: host.Frame.TryWrite(
                    key: key,
                    reason: out var codeReason,
                    row: codeRow,
                    value: code,
                    write: StateWriteKind.Set
                ),
                userMessage: codeReason
            );
            Assert.True(
                condition: host.Frame.TryWrite(
                    key: key,
                    reason: out var reason,
                    row: tokenRow,
                    value: ((code == 0)
                ? -1
                : square),
                    write: StateWriteKind.Set
                ),
                userMessage: reason
            );
        }
        // Compile the shipped judge once, then exhaust every origin/target/piece/colour
        // through its real frame evaluator. The expected answer uses coordinate rays.
        for (var side = 0; (side < 2); side++) {
            var sign = ((side == 0)
                ? -1
                : 1
            );

            for (var king = 0; (king < 64); king++) {
                Put(
                    code: (-sign * 6),
                    square: king
                );
                for (var piece = 1; (piece <= 6); piece++) {
                    for (var target = 0; (target < 64); target++) {
                        if (target == king) { continue; }
                        Put(
                            code: (sign * piece),
                            square: target
                        );
                        host.Judge(
                            rules: rules,
                            tick: 1
                        );
                        Assert.True(condition: host.Frame.TryStored(
                            check,
                            CellName.Parse(candidate: side.ToString()),
                            out var actual,
                            out _
                        ));
                        Assert.Equal(
                            (Attacked(
                                attackerSign: sign,
                                board: board,
                                king: king
                            )
                            ? 1L
                            : 0L),
                            actual
                        );
                        Put(
                            code: 0,
                            square: target
                        );
                    }
                }
                Put(
                    code: 0,
                    square: king
                );
            }
        }
        Assert.Equal(
            0,
            host.Refusals
        );
    }
    [InlineData(-1)]
    [InlineData(1)]
    [Theory]
    public void ChessAttacksRespectEveryOriginIncludingEdgesAndTheSignBit(int sign) {
        int[] offsets = [9, 17, 18, 16, 24, 1];

        for (var king = 0; (king < 64); king++) {
            for (var piece = 1; (piece <= 6); piece++) {
                var board = new long[64];

                board[king] = (-sign * 6);
                // Deliberately wrap the numeric index: the coordinate oracle rejects false edge neighbours.
                board[(((king - (sign * offsets[(piece - 1)])) + 64) % 64)] = (sign * piece);
                CheckChess(board: board);
            }
        }
    }
    [Fact]
    public void ChessSlidersStopAtTheFirstPieceAndMissingKingsAreNotInCheck() {
        CheckChess(board: new long[64]);
        foreach (var sign in new[] { -1, 1 }) {
            foreach (var (dx, dy) in new[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) }) {
                foreach (var blocker in new[] { 0, -sign, (sign * 2), (sign * 5) }) {
                    var board = new long[64];

                    board[27] = (-sign * 6);
                    board[(27 + (3 * (dx + (8 * dy))))] = (sign * (((dx != 0) && (dy != 0))
                        ? 3
                        : 4));
                    board[((27 + dx) + (8 * dy))] = blocker;
                    CheckChess(board: board);
                }
            }
        }
    }
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void QubicRecognizesAll76LinesAndRejectsEveryThreeMarkNearMiss(int player) {
        var lines = Lines().ToArray();

        Assert.Equal(
            76,
            lines.Length
        );
        foreach (var line in lines) {
            var board = new long[64];

            foreach (var cell in line) { board[cell] = player; }
            Assert.Equal(
                player,
                QubicWinner(board: board)
            );
            foreach (var missing in line) {
                board[missing] = 0;
                Assert.Equal(
                    0,
                    QubicWinner(board: board)
                );
                board[missing] = player;
            }
        }
        // Four consecutive integers across a row boundary are not a geometric line.
        var wrap = new long[64];

        foreach (var cell in new[] { 2, 3, 4, 5 }) { wrap[cell] = player; }
        Assert.Equal(
            0,
            QubicWinner(board: wrap)
        );
    }

    private RuleFrameFixture? m_chessJudge;
    private RuleFrameFixture? m_qubicJudge;
}
