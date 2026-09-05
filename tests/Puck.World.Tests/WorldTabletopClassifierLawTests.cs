using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the tabletop classifier's mask/popCount/lowestSetBit technique — reading a side's occupancy as a
/// `$board:mask` bitboard and walking the XOR of two settles rather than comparing each piece's own cell — sorts a
/// synthetic delta into the same shapes the garden's chess rules read: a quiet move (one square vacated and
/// occupied), a capture (a second side's square vacated too, at the destination), an en passant (the second side's
/// vacated square is adjacent to the destination AND the mover carries the pawn code — not merely "not the
/// destination", which is a loophole any unrelated simultaneous other-side vacate falls through), a castle
/// (two-and-two on one side), and a perturbation (any other shape, including a same-shaped capture that fails the
/// en passant adjacency/mover test) — proved once by breaking each shape into the one before it. Also proves the
/// empty-mask clamp: `trailingZeroCount` of a mask with nothing set reads 64 (the mask's own bit width), which is
/// outside every one of these cells' declared ranges and gets refused rather than silently written — so an
/// occupy-only or vacate-only settle must clamp to -1 before writing, exactly as the garden's own
/// cWhite/cBlackFromCell/ToCell/CapturedCell rows now do.
/// <para>The shipped-document cases at the end load those classifier rules directly from
/// <c>src/Puck.World/Assets/worlds/puck.world.json</c>; the synthetic cases above retain small, readable controls,
/// while the shipped cases prevent either the authored expressions or their declaration order from drifting.</para>
/// </summary>
public sealed class WorldTabletopClassifierLawTests {
    private const string Board = "board";
    private const string Prev = "previousBoard";

    private static WorldValueToken[] Eq(WorldValueToken[] a, long v) => [.. a, new WorldValueToken.Constant(v), new WorldValueToken.Equal()];
    private static WorldValueToken[] Eq(WorldValueToken[] a, WorldValueToken[] b) => [.. a, .. b, new WorldValueToken.Equal()];
    private static WorldValueToken[] Mul(WorldValueToken[] a, WorldValueToken[] b) => [.. a, .. b, new WorldValueToken.Multiply()];
    private static WorldValueToken[] Row(string name) => [new WorldValueToken.State(name)];
    // Reads `row[<key-row>'s own SlotKey cell]` -- the same dynamic ($cell:<row>:<key>) indirection the garden's
    // board.$cell:cWhiteToCell:$value reads carry, spelled directly through WorldValueToken.State's own Key rather
    // than the JSON convenience string.
    private static WorldValueToken[] RowAt(string row, string keyRowSlotName) => [new WorldValueToken.State(row, $"$cell:{keyRowSlotName}:{WorldStateRow.SlotKey}")];
    private static WorldValueToken[] Const(long v) => [new WorldValueToken.Constant(v)];
    private static WorldValueToken[] PopcountOf(string maskRow) => [.. Row(maskRow), new WorldValueToken.PopCount()];
    private static WorldValueToken[] Sig(long ownVac, long ownOcc, long otherVac, long otherOcc) => Mul(
        Mul(Eq(PopcountOf("ownVac"), ownVac), Eq(PopcountOf("ownOcc"), ownOcc)),
        Mul(Eq(PopcountOf("otherVac"), otherVac), Eq(PopcountOf("otherOcc"), otherOcc)));
    // Range 1:2 stands in for the garden's own 1:6 (every own piece kind, pawn included) -- wide enough that a
    // non-pawn own piece (code 2) still registers as "own", the same way the garden's mask range admits every
    // piece kind while a narrower per-shape test (the en passant mover check below) picks out the pawn alone.
    private static WorldValueToken[] OwnVacMask() => [new WorldValueToken.State($"$board:mask:{Prev}:1:2"), new WorldValueToken.State($"$board:mask:{Board}:1:2"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OwnOccMask() => [new WorldValueToken.State($"$board:mask:{Board}:1:2"), new WorldValueToken.State($"$board:mask:{Prev}:1:2"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OtherVacMask() => [new WorldValueToken.State($"$board:mask:{Prev}:-1:-1"), new WorldValueToken.State($"$board:mask:{Board}:-1:-1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OtherOccMask() => [new WorldValueToken.State($"$board:mask:{Board}:-1:-1"), new WorldValueToken.State($"$board:mask:{Prev}:-1:-1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] TZ(WorldValueToken[] m) => [.. m, new WorldValueToken.TrailingZeroCount()];
    // The clamp finding #4/#8 add to the garden's own cWhite/cBlackFromCell/ToCell/CapturedCell and kingCell rows:
    // an empty mask's trailingZeroCount (64, the mask's own bit width) never gets written raw -- a mask with
    // nothing set reads as -1 ("no such cell") instead of leaking 64 into a row whose declared range refuses it.
    private static WorldValueToken[] ClampedTZ(WorldValueToken[] m) => Select(Eq(PopcountOf2(m), 0), Const(-1), TZ(m));
    private static WorldValueToken[] PopcountOf2(WorldValueToken[] m) => [.. m, new WorldValueToken.PopCount()];
    private static WorldValueToken[] Select(WorldValueToken[] c, WorldValueToken[] t, WorldValueToken[] f) => [.. c, .. t, .. f, new WorldValueToken.Select()];
    private static ActionEffect.SetState Set(string state, WorldValueToken[] expr) => new(State: state, Expression: new WorldValueExpression(expr));

    // kind: 0 none, 1 quiet, 2 capture, 3 en passant, 4 castle, 7 perturbation — the same codes the garden authors.
    // Split across several small rules, exactly as the garden's own classify-*-masks/counts/signatures/pick rules
    // are: one nested 64-token expression cannot hold the mask arithmetic AND four popCount signatures at once.
    // The captured-adjacent-to-destination test stands in for the garden's own `$board:neighbour` probe (this
    // synthetic 4-wide grid has no pawn geometry to name a "behind" direction against) — "capturedCell is one W
    // step from toCell" plays the same structural role: without it (or the mover-code test beside it), ANY
    // unrelated other-side vacate anywhere on the board would forge a kind-3 record, which is exactly finding #1's
    // loophole.
    private static WorldRule[] ClassifyRules() => [
        new(WorldCellName.Parse("masks"), [
            Set("ownVac", OwnVacMask()), Set("ownOcc", OwnOccMask()), Set("otherVac", OtherVacMask()), Set("otherOcc", OtherOccMask()),
        ]),
        new(WorldCellName.Parse("sigs"), [
            Set("quiet", Sig(1, 1, 0, 0)), Set("capture", Sig(1, 1, 1, 0)),
            Set("castle", Sig(2, 2, 0, 0)), Set("noChange", Sig(0, 0, 0, 0)),
        ]),
        new(WorldCellName.Parse("cellsFrom"), [Set("fromCell", ClampedTZ(Row("ownVac")))]),
        new(WorldCellName.Parse("cellsTo"), [Set("toCell", ClampedTZ(Row("ownOcc")))]),
        new(WorldCellName.Parse("cellsCaptured"), [Set("capturedCell", ClampedTZ(Row("otherVac")))]),
        new(WorldCellName.Parse("kindRule"), [
            Set("kind", Select(Row("quiet"), Const(1),
                Select(Row("capture"),
                    Select(Eq(Row("capturedCell"), Row("toCell")), Const(2),
                        Select(
                            Mul(
                                Eq(Row("capturedCell"), [.. Row("toCell"), .. Const(1), new WorldValueToken.Subtract()]),
                                Eq(RowAt(Board, "toCell"), Const(1))),
                            Const(3), Const(7))),
                    Select(Row("castle"), Const(4),
                        Select(Row("noChange"), Const(0), Const(7)))))),
        ]),
    ];

    private static WorldDefinition Document(long[] previous, long[] current) {
        var document = Fixtures.BuildDocument();
        var topology = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, 4, 4);
        WorldStateCell[] Cells(long[] values) => [.. Enumerable.Range(0, 16).Select(i => new WorldStateCell(WorldCellName.Parse(i.ToString()), values[i]))];
        WorldStateRow Slot(string name, long min, long max) => new(WorldCellName.Parse(name), CellKind.Int, Min: min, Max: max, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0)]);

        return document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(WorldCellName.Parse(Board), CellKind.Int, Min: -6, Max: 6, Cells: Cells(current), Domain: new WorldStateDomain.CellsOf("board")),
                new WorldStateRow(WorldCellName.Parse(Prev), CellKind.Int, Min: -6, Max: 6, Cells: Cells(previous), Domain: new WorldStateDomain.CellsOf("board")),
                Slot("ownVac", 0, 65535), Slot("ownOcc", 0, 65535), Slot("otherVac", 0, 65535), Slot("otherOcc", 0, 65535),
                Slot("quiet", 0, 1), Slot("capture", 0, 1), Slot("castle", 0, 1), Slot("noChange", 0, 1),
                Slot("fromCell", -6, 16), Slot("toCell", -6, 16), Slot("capturedCell", -6, 16),
                Slot("kind", 0, 7),
            ], Lattices: [topology]),
            Rules = ClassifyRules(),
        };
    }

    private static long Cell(long[] previous, long[] current, string row) {
        using var fixture = Fixtures.FreshServer(definition: Document(previous, current));
        fixture.Step();
        return WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
    }
    private static long Kind(long[] previous, long[] current) => Cell(previous, current, "kind");

    private static readonly long[] Start = [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0];

    private static readonly WorldDefinition Garden = LoadGarden();
    private static readonly string[] ShippedClassifierRuleNames = [
        "tabletop-king-cell",
        "tabletop-mover-color",
        "tabletop-classify-white-masks",
        "tabletop-classify-white-counts",
        "tabletop-classify-white-signatures",
        "tabletop-classify-white-pick",
        "tabletop-classify-black-masks",
        "tabletop-classify-black-counts",
        "tabletop-classify-black-signatures",
        "tabletop-classify-black-pick",
        "tabletop-classify-pick",
        "tabletop-classify-mover",
        "tabletop-classify-captured",
    ];

    private static WorldDefinition LoadGarden() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Puck.World", "Assets", "worlds", "puck.world.json");
        Assert.True(WorldDefinitionFileSource.TryLoad(path, out var definition, out _, out var reason), reason);
        return definition!;
    }

    private static WorldDefinition ShippedDocument(long[] previous, long[] current) {
        WorldStateCell[] Cells(long[] values) => [.. values.Select((value, index) =>
            new WorldStateCell(Key: WorldCellName.Parse(index.ToString()), Value: value))];

        // Every classifier rule gates on 'settleHold' reaching its margin (see chess.world.json's own remarks) —
        // seeded already-at-margin here rather than pulling in tabletop-settle-hold-advance/-reset too, since this
        // fixture carries no rigid bodies for that debounce to measure and a single fixture.Step() is meant to
        // classify immediately.
        var rows = Garden.State.Where(row =>
            row.Name.Value is Board or Prev or "kingCell" or "move" or "moverColor" or "settleHold" or "cFromBlack" or "cFromWhite" or "cKindBlack" or "cKindWhite" or "cToBlack" or "cToWhite" ||
            row.Name.Value.StartsWith("cBlack", StringComparison.Ordinal) ||
            row.Name.Value.StartsWith("cWhite", StringComparison.Ordinal)
        ).Select(row => row.Name.Value switch {
            Board => row with { Cells = Cells(current) },
            Prev => row with { Cells = Cells(previous) },
            "settleHold" => row with { Cells = [new WorldStateCell(WorldStateRow.SlotKey, 60)] },
            _ => row,
        }).ToArray();
        var source = Fixtures.BuildDocument();

        return source with {
            StateRaw = new WorldStateSection(
                World: rows,
                Lattices: [Garden.StateRaw!.Lattices!.Single(topology => topology.Name == "chessBoard")]
            ),
            Rules = [.. Garden.Rules!.Where(rule => ShippedClassifierRuleNames.Contains(rule.Name.Value, StringComparer.Ordinal))],
        };
    }

    private static (long Kind, long From, long To, long Mover, long Captured) ShippedMove(long[] previous, long[] current) {
        using var fixture = Fixtures.FreshServer(definition: ShippedDocument(previous, current));
        fixture.Step();
        var move = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "move")!;
        long Read(string key) => WorldDefinitionRows.FindCell(move.Cells, WorldCellName.Parse(key))!.Value;
        return (Read("kind"), Read("from"), Read("to"), Read("mover"), Read("captured"));
    }

    [Fact]
    public void AQuietMoveIsOneSquareVacatedAndOccupiedByTheSameSide() {
        var moved = (long[])Start.Clone();
        moved[0] = 0; moved[2] = 1; // cell0 -> cell2
        Assert.Equal(1L, Kind(Start, moved));
    }

    [Fact]
    public void ACaptureLandsOnTheOtherSidesOwnSquare_ControlItDiffersFromAQuietMoveOnlyByTheDefendersVacate() {
        var captured = (long[])Start.Clone();
        captured[0] = 0; captured[12] = 1; // white takes the piece that stood on cell12
        Assert.Equal(2L, Kind(Start, captured));

        // the discriminating control: the SAME destination without a defender vacating reads as an ordinary quiet
        // move instead — proving kind=2 depends on the captured side's own delta, not merely landing "somewhere".
        var quietToSameCell = (long[])Start.Clone();
        quietToSameCell[0] = 0; quietToSameCell[3] = 1;
        Assert.Equal(1L, Kind(Start, quietToSameCell));
    }

    [Fact]
    public void AnEnPassantLandsBesideTheCapturedSquareRatherThanOnIt() {
        var ep = (long[])Start.Clone();
        ep[1] = 0; ep[13] = 1; ep[12] = 0; // white moves cell1->cell13; the defender vacates cell12, not the (empty) destination cell13
        Assert.Equal(3L, Kind(Start, ep));

        // the discriminating control: capturedCell not adjacent to toCell — an unrelated other-side vacate
        // anywhere else on the board — reads as a perturbation, never a forged en passant.
        var unrelated = (long[])Start.Clone();
        unrelated[1] = 0; unrelated[9] = 1; unrelated[12] = 0; // own moves cell1->cell9; defender vacates cell12, two rows off cell9
        Assert.Equal(7L, Kind(Start, unrelated));
    }

    [Fact]
    public void AnEnPassantAlsoRequiresTheMoverToCarryThePawnCode() {
        var nonPawnMover = (long[])Start.Clone();
        nonPawnMover[1] = 0; nonPawnMover[13] = 2; nonPawnMover[12] = 0; // adjacency holds, but the landed value (2) is not the pawn code (1)
        Assert.Equal(7L, Kind(Start, nonPawnMover));
    }

    [Fact]
    public void AnOccupyOnlySettleClampsItsEmptySideToMinusOneRatherThanLeakingTrailingZeroCount() {
        // own occupies cell5 with nothing vacating -- ownVac stays empty, so an unclamped trailingZeroCount would
        // write 64 (the mask's own bit width) into fromCell, which every one of its declared ranges refuses.
        var occupyOnly = (long[])Start.Clone();
        occupyOnly[5] = 1;
        Assert.Equal(7L, Kind(Start, occupyOnly));
        Assert.Equal(-1L, Cell(Start, occupyOnly, "fromCell"));

        // the dual: own vacates cell0 with nothing new occupied -- ownOcc stays empty, clamping toCell the same way.
        var vacateOnly = (long[])Start.Clone();
        vacateOnly[0] = 0;
        Assert.Equal(7L, Kind(Start, vacateOnly));
        Assert.Equal(-1L, Cell(Start, vacateOnly, "toCell"));
    }

    [Fact]
    public void ACastleIsTwoAndTwoOnOneSideWithTheOtherUntouched() {
        var castled = (long[])Start.Clone();
        castled[0] = 0; castled[1] = 0; castled[2] = 1; castled[3] = 1;
        Assert.Equal(4L, Kind(Start, castled));
    }

    [Fact]
    public void NothingChangingReadsAsNoMoveAndAnythingElseReadsAsAPerturbation() {
        Assert.Equal(0L, Kind(Start, Start));

        // three squares changing on one side matches none of the four shapes (own vacates 2, occupies only 1).
        var chaos = (long[])Start.Clone();
        chaos[0] = 0; chaos[1] = 0; chaos[2] = 1;
        Assert.Equal(7L, Kind(Start, chaos));
    }

    [Fact]
    public void ShippedGardenRulesClassifyQuietCaptureAndCastleInDeclarationOrder() {
        var quietBefore = new long[64];
        quietBefore[8] = 1;
        var quietAfter = (long[])quietBefore.Clone();
        quietAfter[8] = 0;
        quietAfter[16] = 1;
        Assert.Equal(expected: (1L, 8L, 16L, 1L, 0L), actual: ShippedMove(quietBefore, quietAfter));

        var captureBefore = new long[64];
        captureBefore[8] = 1;
        captureBefore[17] = -1;
        var captureAfter = (long[])captureBefore.Clone();
        captureAfter[8] = 0;
        captureAfter[17] = 1;
        Assert.Equal(expected: (2L, 8L, 17L, 1L, -1L), actual: ShippedMove(captureBefore, captureAfter));

        var castleBefore = new long[64];
        castleBefore[4] = 6;
        castleBefore[7] = 4;
        var castleAfter = (long[])castleBefore.Clone();
        castleAfter[4] = 0;
        castleAfter[7] = 0;
        castleAfter[5] = 4;
        castleAfter[6] = 6;
        Assert.Equal(expected: (4L, 4L, 6L, 6L, 0L), actual: ShippedMove(castleBefore, castleAfter));
    }

    [Fact]
    public void ShippedGardenEnPassantRequiresTheCapturedPawnBehindTheDestination() {
        var before = new long[64];
        before[36] = 1;
        before[35] = -1;
        var enPassant = (long[])before.Clone();
        enPassant[36] = 0;
        enPassant[35] = 0;
        enPassant[43] = 1;
        Assert.Equal(expected: (3L, 36L, 43L, 1L, -1L), actual: ShippedMove(before, enPassant));

        var unrelatedVacate = (long[])before.Clone();
        unrelatedVacate[36] = 0;
        unrelatedVacate[35] = 0;
        unrelatedVacate[44] = 1;
        Assert.Equal(expected: 7L, actual: ShippedMove(before, unrelatedVacate).Kind);
    }
}
