using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises the shipped forward judge with complete observations, independently constructed moves,
/// and adversarial changes outside their footprints. Physical sampling is covered by ChessModuleImportLawTests.</summary>
public sealed class WorldTabletopMatcherLawTests {
    private static readonly WorldDefinition Garden = Load();
    private static WorldDefinition Load() {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Puck.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        Assert.True(WorldDefinitionLoader.TryLoadFile(Path.Combine(root!.FullName,
            "tests/Puck.World.Tests/Fixtures/minimal-chess-host.world.json"), out var result, out var reason), reason);
        return result!;
    }

    [Theory]
    [InlineData(false, false, false, 0L)]
    [InlineData(true, false, false, 1L)]
    [InlineData(true, true, false, 1L)]
    [InlineData(true, false, true, 2L)]
    public void SamplingCountsExcessOccupantsEvenWhenTheirCodesAreIdentical(bool overlap, bool mixedCodes, bool triple, long expected) {
        var keys = new[] { "piece0", "piece1", "piece2", "piece3" };
        StateCell[] Keyed(params long[] values) => [.. keys.Select((key, i) => new StateCell(CellName.Parse(key), values[i]))];
        var locations = Keyed(0, overlap ? 0 : 7, triple ? 0 : 63, 8);
        var code = Garden.State.Single(r => r.Name.Value == "pieceCode") with { Cells = Keyed(1, mixedCodes ? -1 : 1, 1, 4) };
        var cell = Garden.State.Single(r => r.Name.Value == "pieceCell") with { Cells = Keyed(-1, -1, -1, -1) };
        var upright = new ActionPredicate.CompareState(State: "sampleUpright", Key: "$each", Comparison: ActionStateComparison.Equal, Value: 1);
        // Substitute only the physical input boundary. Keep the shipped sampling effects and their rule order.
        var rules = Garden.Rules!.Where(r => r.Name.Value is
            "tabletop-derive-cell-upright" or "tabletop-derive-cell-tilted" or "tabletop-board-collisions")
            .Select(r => r.Name.Value switch {
                "tabletop-derive-cell-upright" => r with {
                    Gate = upright,
                    Effects = [.. r.Effects.Select(e => e is ActionEffect.SetState s && s.State == "pieceCell"
                        ? s with { FromState = "sampleCell", FromKey = "$each" } : e)],
                },
                "tabletop-derive-cell-tilted" => r with { Gate = new ActionPredicate.Not(upright) },
                _ => r,
            }).ToArray();
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [.. Garden.State.Select(r => r.Name.Value switch {
                "pieceCode" => code, "pieceCell" => cell, "settleHold" => Slot(r, 60), _ => r,
            }), cell with { Name = CellName.Parse("sampleCell"), Cells = locations },
                cell with { Name = CellName.Parse("sampleUpright"), Cells = Keyed(1, 1, 1, 0) }],
                Lattices: Garden.StateRaw!.Lattices),
            Rules = rules,
        };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.Step();
        Assert.Equal(expected, Read(fixture, "boardCollisions"));
        Assert.Equal(-1, Read(fixture, "pieceCell", "piece3"));
    }

    private static StateCell[] Cells(long[] board) => [.. board.Select((v, i) => new StateCell(CellName.Parse(i.ToString()), v))];
    // The board is derived from its token rows (inverse), so a position is seeded as tokens: one token per occupied
    // cell in cell order, the rest off the board.
    private static (StateCell[] Cells, StateCell[] Codes) Tokens(long[] board) {
        var cells = new StateCell[32];
        var codes = new StateCell[32];
        var next = 0;
        for (var cell = 0; cell < board.Length; cell++) {
            if (board[cell] != 0) {
                cells[next] = new StateCell(CellName.Parse($"piece{next}"), cell);
                codes[next] = new StateCell(CellName.Parse($"piece{next}"), board[cell]);
                next++;
            }
        }
        for (; next < 32; next++) {
            cells[next] = new StateCell(CellName.Parse($"piece{next}"), -1);
            codes[next] = new StateCell(CellName.Parse($"piece{next}"), 0);
        }
        return (cells, codes);
    }
    private static long[] Position(params (int Cell, int Piece)[] pieces) {
        var board = new long[64];
        board[4] = 6; board[60] = -6;
        foreach (var (cell, piece) in pieces) { board[cell] = piece; }
        return board;
    }
    private static long[] Move(long[] board, int from, int to, int promotion = 0) {
        var after = (long[])board.Clone(); after[to] = promotion == 0 ? board[from] : promotion; after[from] = 0;
        return after;
    }
    private static long[] Mirror(long[] board, int sign) {
        var result = new long[64];
        for (var i = 0; i < 64; i++) { result[i ^ (sign < 0 ? 56 : 0)] = board[i] * sign; }
        return result;
    }

    private static WorldDefinition Judge(long[] before, long[] after, int turn = 0, int ep = -1, int rights = 0, int collisions = 0) {
        string[] sampled = ["tabletop-settle-hold-advance", "tabletop-settle-hold-reset",
            "tabletop-derive-cell-upright", "tabletop-derive-cell-tilted",
            "tabletop-game-start-snapshot", "tabletop-game-start-check", "tabletop-board-collisions"];
        var (tokenCells, tokenCodes) = Tokens(after);
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [.. Garden.State.Select(row => row.Name.Value switch {
                "pieceCell" => row with { Cells = tokenCells },
                "pieceCode" => row with { Cells = tokenCodes },
                "lastLegal" => row with { Cells = Cells(before) },
                "settleHold" => Slot(row, 60), "gameStarted" => Slot(row, 1), "turn" => Slot(row, turn),
                "enPassantTarget" => Slot(row, ep), "castleRights" => Slot(row, rights),
                "boardCollisions" => Slot(row, collisions),
                "previousInCheck" => row with { Cells = [new StateCell(CellName.Parse("0"), Attacked(before, Array.IndexOf(before, 6L), -1) ? 1 : 0),
                    new StateCell(CellName.Parse("1"), Attacked(before, Array.IndexOf(before, -6L), 1) ? 1 : 0)] },
                _ => row,
            })], Lattices: Garden.StateRaw!.Lattices),
            PatternsRaw = Garden.Patterns,
            Rules = [.. Garden.Rules!.Where(r => !sampled.Contains(r.Name.Value))],
        };
    }
    private static WorldStateRow Slot(WorldStateRow row, long value) => row with { Cells = [new StateCell(WorldStateRow.SlotKey, value)] };
    private static long Read(WorldFixture fixture, string row, string key = "$value") {
        var state = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!;
        return state.Cells!.SingleOrDefault(c => c.Key.Value == key)?.Value ?? 0;
    }
    private RuleFrameFixture? m_judge;
    private static long Read(RuleFrameFixture fixture, string row, string key = "$value") => fixture.Read(row, key);
    private void Check(long[] before, long[] after, bool legal, int turn = 0, int ep = -1, int rights = 0, int collisions = 0) {
        var position = Judge(before, after, turn, ep, rights, collisions);
        var fixture = m_judge ??= new RuleFrameFixture(position);
        fixture.Evaluate(position);
        Assert.Equal(legal ? 1 : 0, Read(fixture, "verdict"));
        Assert.Equal(legal ? 1 - turn : turn, Read(fixture, "turn"));
        for (var i = 0; i < 64; i++) { Assert.Equal((legal ? after : before)[i], Read(fixture, "lastLegal", i.ToString())); }
    }

    // Coordinate rays and deltas deliberately share no engine directions, bit masks, patterns, or query calls.
    private static bool Attacked(long[] board, int king, int sign) {
        if (king < 0) { return false; }
        for (var cell = 0; cell < 64; cell++) {
            if (Math.Sign(board[cell]) != sign) { continue; }
            var dx = king % 8 - cell % 8; var dy = king / 8 - cell / 8;
            var x = Math.Abs(dx); var y = Math.Abs(dy); var piece = Math.Abs(board[cell]);
            if (piece == 1 && x == 1 && dy == sign || piece == 2 && x * y == 2 || piece == 6 && Math.Max(x, y) == 1) { return true; }
            if (!(piece is 3 or 5 && x == y && x != 0 || piece is 4 or 5 && (x == 0) != (y == 0))) { continue; }
            var step = Math.Sign(dx) + 8 * Math.Sign(dy);
            var probe = cell + step;
            while (probe != king && board[probe] == 0) { probe += step; }
            if (probe == king) { return true; }
        }
        return false;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void EveryOrdinaryDestinationAgreesWithCoordinateGeometry(int sign) {
        for (var piece = 1; piece <= 6; piece++) {
            var before = Position((27, piece));
            if (piece == 6) { before[4] = 0; }
            for (var to = 0; to < 64; to++) {
                if (to == 27 || before[to] != 0) { continue; }
                var after = Move(before, 27, to);
                var dx = Math.Abs(to % 8 - 3); var dy = to / 8 - 3;
                var geometry = piece switch {
                    1 => dx == 0 && dy == 1,
                    2 => dx * Math.Abs(dy) == 2,
                    3 => dx == Math.Abs(dy),
                    4 => (dx == 0) != (dy == 0),
                    5 => dx == Math.Abs(dy) || (dx == 0) != (dy == 0),
                    _ => Math.Max(dx, Math.Abs(dy)) == 1,
                };
                Check(Mirror(before, sign), Mirror(after, sign), geometry && !Attacked(after, Array.IndexOf(after, 6L), -1), sign < 0 ? 1 : 0);
            }
        }
    }

    [Fact]
    public void InitialPositionHasTwentyAcceptedMoves() {
        var before = new long[64];
        int[] back = [4, 2, 3, 5, 6, 3, 2, 4];
        for (var i = 0; i < 8; i++) { before[i] = back[i]; before[i + 8] = 1; before[i + 48] = -1; before[i + 56] = -back[i]; }
        var accepted = 0;
        var fixture = new RuleFrameFixture(Judge(before, before));
        for (var from = 0; from < 16; from++) {
            for (var to = 16; to < 48; to++) {
                fixture.Evaluate(Judge(before, Move(before, from, to)));
                if (Read(fixture, "verdict") == 1) { accepted++; }
            }
        }
        Assert.Equal(20, accepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void PromotionsRequireACompleteFriendlyReplacement(int sign) {
        var before = Position((48, 1));
        for (var code = -6; code <= 6; code++) {
            var after = Move(before, 48, 56); after[56] = code;
            Check(Mirror(before, sign), Mirror(after, sign), code is >= 2 and <= 5, sign < 0 ? 1 : 0);
        }
        // Capturing promotion is the same ply, with the pawn's geometry and the replacement's resulting attacks.
        before[57] = -4;
        for (var code = 2; code <= 5; code++) {
            Check(Mirror(before, sign), Mirror(Move(before, 48, 57, code), sign), true, sign < 0 ? 1 : 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void EnPassantRequiresTheExactVictimAndCannotExposeTheKing(int sign) {
        var before = Position((36, 1), (35, -1));
        var after = Move(before, 36, 43); after[35] = 0;
        var turn = sign < 0 ? 1 : 0; var ep = 43 ^ (sign < 0 ? 56 : 0);
        Check(Mirror(before, sign), Mirror(after, sign), true, turn, ep);
        Check(Mirror(before, sign), Mirror(after, sign), false, turn);
        after[35] = -1;
        Check(Mirror(before, sign), Mirror(after, sign), false, turn, ep);
        before[4] = 0; before[39] = 6; before[32] = -4;
        after = Move(before, 36, 43); after[35] = 0;
        Check(Mirror(before, sign), Mirror(after, sign), false, turn, ep);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    public void CastlesNeedTheRightRookPathAndRights(int sign, bool queenside) {
        var rook = queenside ? 0 : 7; var target = queenside ? 2 : 6; var transit = queenside ? 3 : 5;
        var before = Position((rook, 4)); var after = Move(before, 4, target); after[rook] = 0; after[transit] = 4;
        var turn = sign < 0 ? 1 : 0; var rights = (queenside ? 1 : 2) << (2 * turn);
        Check(Mirror(before, sign), Mirror(after, sign), true, turn);
        Check(Mirror(before, sign), Mirror(after, sign), false, turn, rights: rights);
        after[transit] = 0; after[transit + 8] = 4;
        Check(Mirror(before, sign), Mirror(after, sign), false, turn);
    }

    [Fact]
    public void EveryUnrelatedChangedSquareAndSameColourTransmutationIsRefused() {
        var before = Position((12, 1), (1, 2), (56, -4)); var after = Move(before, 12, 28);
        Check(before, after, true);
        for (var cell = 0; cell < 64; cell++) {
            if (cell is 12 or 28) { continue; }
            var bad = (long[])after.Clone(); bad[cell] = bad[cell] == 0 ? -3 : -bad[cell];
            Check(before, bad, false);
        }
        after[1] = 3; Check(before, after, false); after[1] = 2;
        after[28] = 5; Check(before, after, false); after[28] = 1;
        Check(before, after, false, collisions: 1);
        Check(before, before, false);
        Check(before, Move(before, 56, 48), false); // Wrong side.
        Check(Position((52, 4)), Move(Position((52, 4)), 52, 60), false); // A king cannot be captured.
    }

    [Fact]
    public void AcceptedRookMovesConsumeOnlyTheirOwnRightsAndRefusalsConsumeNone() {
        var before = Position((7, 4));
        using var legal = Fixtures.FreshServer(Judge(before, Move(before, 7, 15)));
        legal.Step(); Assert.Equal(2, Read(legal, "castleRights"));
        using var illegal = Fixtures.FreshServer(Judge(before, Move(before, 7, 14)));
        illegal.Step(); Assert.Equal(0, Read(illegal, "castleRights"));
        var missing = (long[])before.Clone(); missing[7] = 0;
        Check(before, missing, false);
        before[15] = -4;
        using var capture = Fixtures.FreshServer(Judge(before, Move(before, 15, 7), turn: 1));
        capture.Step(); Assert.Equal(1, Read(capture, "verdict")); Assert.Equal(2, Read(capture, "castleRights"));
    }

    [Fact]
    public void CastlingRightsProjectEveryHomeOccupantLossAndPreserveConsumedRights() {
        (int Cell, int Piece, int Rights)[] homes = [(0, 4, 1), (4, 6, 3), (7, 4, 2),
            (56, -4, 4), (60, -6, 12), (63, -4, 8)];
        var before = Position((0, 4), (7, 4), (56, -4), (63, -4));
        RuleFrameFixture? judge = null;
        void CheckRights(long[] after, int rights) {
            var source = Judge(before, after, rights: rights);
            var expected = rights;
            foreach (var home in homes) {
                if (before[home.Cell] == home.Piece && after[home.Cell] != home.Piece) { expected |= home.Rights; }
            }
            var position = source with {
                StateRaw = source.StateRaw! with { World = [.. source.State.Select(row => row.Name.Value switch {
                    "verdict" => Slot(row, 1),
                    "move" => row with { Cells = [.. row.Cells!.Select(c => c.Key.Value == "kind" ? c with { Value = 1 } : c)] },
                    _ => row,
                })] },
                Rules = [.. source.Rules!.Where(r => r.Name.Value == "tabletop-advance-turn")],
            };
            var fixture = judge ??= new RuleFrameFixture(position);
            fixture.Evaluate(position);
            Assert.Equal(expected, Read(fixture, "castleRights"));
            Assert.Equal(1, Read(fixture, "turn"));
        }
        for (var losses = 0; losses < 64; losses++) {
            var after = (long[])before.Clone();
            for (var i = 0; i < homes.Length; i++) {
                if ((losses & (1 << i)) != 0) { after[homes[i].Cell] = 0; }
            }
            for (var rights = 0; rights < 16; rights++) { CheckRights(after, rights); }
        }
        // Replacement is loss too, unless the same home piece still occupies that square.
        foreach (var home in homes) {
            for (var code = -6; code <= 6; code++) {
                var after = (long[])before.Clone();
                after[home.Cell] = code;
                CheckRights(after, 0);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DuplicatePhysicalOccupantsCannotBeHiddenByWriteOrder(bool reverse, bool sameCode) {
        var source = Judge(Position(), new long[64]);
        var codes = new[] { new StateCell(CellName.Parse("piece1"), sameCode ? 1 : 2), new StateCell(CellName.Parse("piece8"), 1) };
        if (reverse) { Array.Reverse(codes); }
        var definition = source with {
            StateRaw = source.StateRaw! with { World = [.. source.State.Select(row => row.Name.Value switch {
                "pieceCode" => row with { Cells = codes },
                "pieceCell" => row with { Cells = [.. codes.Select(c => c with { Value = 28 })] },
                _ => row,
            })] },
            Rules = [.. Garden.Rules!.Where(r => r.Name.Value == "tabletop-board-collisions")],
        };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.Step();
        // The inverse keeps the last token; the census still detects the excess occupant with either code.
        Assert.Equal(codes[^1].Value, Read(fixture, "board", "28"));
        Assert.Equal(1, Read(fixture, "boardCollisions"));
    }

    [Fact]
    public void FootprintCountsAgreeWithAFullBoardComparison() {
        RuleFrameFixture? judge = null;
        void Compare(long[] before, long[] after, int turn) {
            var source = Judge(before, after, turn);
            var position = source with {
                Rules = [.. source.Rules!.Where(r => r.Name.Value is
                    "tabletop-candidate-cells" or "tabletop-candidate-move" or "tabletop-candidate-match")],
            };
            var fixture = judge ??= new RuleFrameFixture(position);
            fixture.Evaluate(position);
            var from = (int)Read(fixture, "move", "from");
            var to = (int)Read(fixture, "move", "to");
            var kind = Read(fixture, "move", "kind");
            var mover = Read(fixture, "move", "mover");
            var expected = (long[])before.Clone();
            // Apply the candidate in reverse priority order. Overlapping or absent patch
            // addresses deliberately exercise malformed candidates as well as legal moves.
            void Write(int cell, long value) { if (cell is >= 0 and < 64) { expected[cell] = value; } }
            if (kind == 3) { Write(to - 8 * (1 - 2 * turn), 0); }
            if (kind == 4) {
                Write((from + to) / 2, 4 * (1 - 2 * turn));
                Write(from / 8 * 8 + (to > from ? 7 : 0), 0);
            }
            Write(to, Math.Abs(mover) == 1 && to >= 0 && to / 8 == (turn == 0 ? 7 : 0) ? after[to] : mover);
            Write(from, 0);
            Assert.True(Enumerable.Range(0, 64).Count(i => before[i] != after[i]) == Read(fixture, "boardChanged"), $"changed count: turn={turn}, from={from}, to={to}, kind={kind}, mover={mover}, actual={Read(fixture, "boardChanged")}");
            Assert.Equal(Enumerable.Range(0, 64).Count(i => expected[i] != after[i]), Read(fixture, "boardMismatch"));
        }

        // Every accepted piece code against every observation code, including empty; both low and sign-bit
        // squares must compare exactly.
        foreach (var cell in new[] { 0, 31, 63 }) {
            for (var previous = -6; previous <= 6; previous++) {
                for (var observed = -6; observed <= 6; observed++) {
                    var before = new long[64]; before[cell] = previous;
                    var after = new long[64]; after[cell] = observed;
                    Compare(before, after, previous < 0 ? 1 : 0);
                }
            }
        }
        for (var turn = 0; turn < 2; turn++) {
            var sign = 1 - 2 * turn;
            for (var from = 0; from < 64; from++) {
                foreach (var piece in new[] { 1, 6 }) {
                    foreach (var delta in new[] { -9, -2, 2, 9 }) {
                        var to = (from + delta + 64) % 64;
                        var before = new long[64]; before[from] = piece * sign;
                        var after = Move(before, from, to);
                        Compare(before, after, turn);
                    }
                }
            }
        }
    }
}
