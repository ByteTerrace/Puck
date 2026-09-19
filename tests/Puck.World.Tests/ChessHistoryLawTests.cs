using Xunit;

namespace Puck.World.Tests;

/// <summary>Exact repetition identity and retention in the shipped chess rules, including legal en passant.</summary>
public sealed class ChessHistoryLawTests {
    private static readonly WorldDefinition Chess = AuthoredGameFixtures.Load(
        relativePath: "src/Puck.World/Assets/worlds/games/chess.world.json"
    );

    private static long[] Position() {
        var board = new long[64];

        board[4] = 6;
        board[60] = -6;
        board[6] = 2;
        board[62] = -2;
        return board;
    }

    private static RuleArenaFixture History() => new(definition: Fixtures.BuildDocument() with {
        StateRaw = Chess.StateRaw! with {
            World = [.. Chess.State.Select(selector: row => row.Name.Value switch {
                "board" => row with {
                    Inverse = null,
                    Cells = [.. Enumerable.Range(start: 0, count: 64).Select(selector: cell => new StateCell(
                        CellName.Parse(candidate: cell.ToString()), CellValue.Int(value: 0)
                    ))],
                },
                "settleHold" => row with { Cells = [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 60))] },
                _ => row,
            })],
        },
        Rules = [.. Chess.Rules!.Where(predicate: rule => (
            rule.Name.Value.StartsWith(value: "tabletop-history-", comparisonType: StringComparison.Ordinal) ||
            (rule.Name.Value == "tabletop-board-history")
        ))],
    });

    private static long Record(RuleArenaFixture fixture, long[] board, int turn = 0, int rights = 0, int enPassant = -1, bool reset = false) {
        for (var cell = 0; (cell < 64); cell++) {
            fixture.Write(row: "board", key: cell.ToString(), value: board[cell]);
        }
        fixture.Write(row: "turn", key: WorldStateRow.SlotKey, value: turn);
        fixture.Write(row: "castleRights", key: WorldStateRow.SlotKey, value: rights);
        fixture.Write(row: "enPassantTarget", key: WorldStateRow.SlotKey, value: enPassant);
        fixture.Write(row: "historyReset", key: WorldStateRow.SlotKey, value: (reset ? 1 : 0));
        fixture.Write(row: "historyPending", key: WorldStateRow.SlotKey, value: 1);
        fixture.Judge();
        return fixture.Read(row: "repetitionCount");
    }

    [Fact]
    public void InitialPositionAndOlderThanTwelvePliesRemainAvailable() {
        var fixture = History();
        var initial = Position();

        Assert.Equal(expected: 1, actual: Record(fixture: fixture, board: initial, reset: true));
        // Independent observations test retention, not move generation. None equals the initial board.
        for (var ply = 1; (ply < 16); ply++) {
            var other = Position();

            other[6] = 0;
            other[16 + ply] = 2;
            Record(fixture: fixture, board: other, turn: (ply & 1));
        }
        Assert.Equal(expected: 2, actual: Record(fixture: fixture, board: initial));
        Record(fixture: fixture, board: initial, turn: 1);
        Assert.Equal(expected: 3, actual: Record(fixture: fixture, board: initial));
        Assert.Equal(expected: 19, actual: fixture.Read(row: "historyLength"));
    }

    [Fact]
    public void EveryPieceCodeAtEverySquareHasDistinctIdentity() {
        // An identity test does not require a playable position. Exercise every bit, including bit 63,
        // and every pair of codes so no same-colour transmutation can disappear between bitplanes.
        var fixture = History();

        for (var cell = 0; (cell < 64); cell++) {
            for (var before = -6; (before <= 6); before++) {
                for (var after = -6; (after <= 6); after++) {
                    var board = new long[64];

                    board[cell] = before;
                    Record(fixture: fixture, board: board, reset: true);
                    Record(fixture: fixture, board: board, turn: 1);
                    board[cell] = after;
                    Assert.Equal(
                        expected: (before == after ? 2 : 1),
                        actual: Record(fixture: fixture, board: board)
                    );
                }
            }
        }
    }

    [Fact]
    public void ReflectionsSideToMoveAndCastlingRightsAreDistinct() {
        var board = Position();
        var reflected = new long[64];

        for (var cell = 0; (cell < 64); cell++) {
            reflected[cell ^ 7] = board[cell];
        }
        foreach (var (observed, turn, rights) in new[] { (reflected, 0, 0), (board, 1, 0), (board, 0, 1) }) {
            var fixture = History();

            Record(fixture: fixture, board: board, reset: true);
            Record(fixture: fixture, board: board, turn: 1);
            Assert.Equal(expected: 1, actual: Record(fixture: fixture, board: observed, turn: turn, rights: rights));
        }
    }

    [InlineData(1)]
    [InlineData(-1)]
    [Theory]
    public void OnlyLegallyAvailableEnPassantChangesIdentity(int sign) {
        void Check(long[] whiteBoard, bool available) {
            var board = new long[64];
            var turn = (sign == 1 ? 0 : 1);
            var target = (43 ^ (turn * 56));

            for (var cell = 0; (cell < 64); cell++) {
                board[cell ^ (turn * 56)] = (whiteBoard[cell] * sign);
            }
            var fixture = History();

            Record(fixture: fixture, board: board, turn: turn, enPassant: target, reset: true);
            Assert.Equal(expected: (available ? target : -1), actual: fixture.Read(row: "repetitionEnPassant"));
            Record(fixture: fixture, board: board, turn: (1 - turn));
            Assert.Equal(expected: (available ? 1 : 2), actual: Record(fixture: fixture, board: board, turn: turn));
        }

        var board = Position();

        board[35] = -1;
        Check(whiteBoard: board, available: false); // No adjacent capturer.
        board[36] = 1;
        Check(whiteBoard: board, available: true);
        board[60] = -4;
        board[63] = -6;
        Check(whiteBoard: board, available: false); // e5 pawn pinned on the e-file.
        board[34] = 1;
        Check(whiteBoard: board, available: true); // The other adjacent pawn can still capture.
        board[34] = 0;
        board[4] = 0;
        board[39] = 6;
        board[32] = -4;
        Check(whiteBoard: board, available: false); // Removing both pawns exposes a rank attack.
        board[32] = 0;
        board[43] = 3;
        Check(whiteBoard: board, available: false); // Target must be empty.
    }

    [Fact]
    public void FullSeventyFiveMoveWindowRetainsItsInitialPositionAndResetExcludesOldEntries() {
        var fixture = History();
        var board = Position();

        for (var ply = 0; (ply <= 150); ply++) {
            Record(fixture: fixture, board: board, turn: (ply & 1), reset: (ply == 0));
        }
        Assert.Equal(expected: 151, actual: fixture.Read(row: "historyLength"));
        Assert.Equal(expected: 76, actual: fixture.Read(row: "repetitionCount"));
        Assert.Equal(expected: 1, actual: Record(fixture: fixture, board: board, reset: true));
        Assert.Equal(expected: 1, actual: fixture.Read(row: "historyLength"));
        Record(fixture: fixture, board: board, turn: 1);
        Assert.Equal(expected: 2, actual: Record(fixture: fixture, board: board));
    }
}
