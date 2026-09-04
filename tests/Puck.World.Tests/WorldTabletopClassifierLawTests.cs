using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the tabletop classifier's mask/popCount/lowestSetBit technique — reading a side's occupancy as a
/// `$board:mask` bitboard and walking the XOR of two settles rather than comparing each piece's own cell — sorts a
/// synthetic delta into the same shapes the garden's chess rules read: a quiet move (one square vacated and
/// occupied), a capture (a second side's square vacated too, at the destination), an en passant (the second side's
/// vacated square is NOT the destination), a castle (two-and-two on one side), and a perturbation (any other
/// shape) — proved once by breaking each shape into the one before it.
/// </summary>
public sealed class WorldTabletopClassifierLawTests {
    private const string Board = "board";
    private const string Prev = "previousBoard";

    private static WorldValueToken[] Eq(WorldValueToken[] a, long v) => [.. a, new WorldValueToken.Constant(v), new WorldValueToken.Equal()];
    private static WorldValueToken[] Mul(WorldValueToken[] a, WorldValueToken[] b) => [.. a, .. b, new WorldValueToken.Multiply()];
    private static WorldValueToken[] Row(string name) => [new WorldValueToken.State(name)];
    private static WorldValueToken[] PopcountOf(string maskRow) => [.. Row(maskRow), new WorldValueToken.PopCount()];
    private static WorldValueToken[] Sig(long ownVac, long ownOcc, long otherVac, long otherOcc) => Mul(
        Mul(Eq(PopcountOf("ownVac"), ownVac), Eq(PopcountOf("ownOcc"), ownOcc)),
        Mul(Eq(PopcountOf("otherVac"), otherVac), Eq(PopcountOf("otherOcc"), otherOcc)));
    private static WorldValueToken[] OwnVacMask() => [new WorldValueToken.State($"$board:mask:{Prev}:1:1"), new WorldValueToken.State($"$board:mask:{Board}:1:1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OwnOccMask() => [new WorldValueToken.State($"$board:mask:{Board}:1:1"), new WorldValueToken.State($"$board:mask:{Prev}:1:1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OtherVacMask() => [new WorldValueToken.State($"$board:mask:{Prev}:-1:-1"), new WorldValueToken.State($"$board:mask:{Board}:-1:-1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] OtherOccMask() => [new WorldValueToken.State($"$board:mask:{Board}:-1:-1"), new WorldValueToken.State($"$board:mask:{Prev}:-1:-1"), new WorldValueToken.BitNot(), new WorldValueToken.BitAnd()];
    private static WorldValueToken[] TZ(WorldValueToken[] m) => [.. m, new WorldValueToken.TrailingZeroCount()];
    private static WorldValueToken[] Select(WorldValueToken[] c, WorldValueToken[] t, WorldValueToken[] f) => [.. c, .. t, .. f, new WorldValueToken.Select()];
    private static ActionEffect.SetState Set(string state, WorldValueToken[] expr) => new(State: state, Expression: new WorldValueExpression(expr));

    // kind: 0 none, 1 quiet, 2 capture, 3 en passant, 4 castle, 7 perturbation — the same codes the garden authors.
    // Split across several small rules, exactly as the garden's own classify-*-masks/counts/signatures/pick rules
    // are: one nested 64-token expression cannot hold the mask arithmetic AND four popCount signatures at once.
    private static WorldRule[] ClassifyRules() => [
        new(WorldCellName.Parse("masks"), [
            Set("ownVac", OwnVacMask()), Set("ownOcc", OwnOccMask()), Set("otherVac", OtherVacMask()), Set("otherOcc", OtherOccMask()),
        ]),
        new(WorldCellName.Parse("sigs"), [
            Set("quiet", Sig(1, 1, 0, 0)), Set("capture", Sig(1, 1, 1, 0)),
            Set("castle", Sig(2, 2, 0, 0)), Set("noChange", Sig(0, 0, 0, 0)),
        ]),
        new(WorldCellName.Parse("cellsFrom"), [Set("fromCell", TZ(Row("ownVac")))]),
        new(WorldCellName.Parse("cellsTo"), [Set("toCell", TZ(Row("ownOcc")))]),
        new(WorldCellName.Parse("cellsCaptured"), [Set("capturedCell", TZ(Row("otherVac")))]),
        new(WorldCellName.Parse("kindRule"), [
            Set("kind", Select(Row("quiet"),
                Select([.. Row("fromCell"), .. Row("toCell"), new WorldValueToken.Equal()], [new WorldValueToken.Constant(6)], [new WorldValueToken.Constant(1)]),
                Select(Row("capture"),
                    Select([.. Row("capturedCell"), .. Row("toCell"), new WorldValueToken.Equal()], [new WorldValueToken.Constant(2)], [new WorldValueToken.Constant(3)]),
                    Select(Row("castle"), [new WorldValueToken.Constant(4)],
                        Select(Row("noChange"), [new WorldValueToken.Constant(0)], [new WorldValueToken.Constant(7)]))))),
        ]),
    ];

    private static WorldDefinition Document(long[] previous, long[] current) {
        var document = Fixtures.BuildDocument();
        var topology = new WorldStateLatticeTopology("board", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid);
        WorldStateCell[] Cells(long[] values) => [.. Enumerable.Range(0, 16).Select(i => new WorldStateCell(WorldCellName.Parse(i.ToString()), values[i]))];
        WorldStateRow Slot(string name, long min, long max) => new(WorldCellName.Parse(name), CellKind.Int, Min: min, Max: max, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0)]);

        return document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(WorldCellName.Parse(Board), CellKind.Int, Min: -6, Max: 6, Cells: Cells(current), Board: new WorldStateBoard("board")),
                new WorldStateRow(WorldCellName.Parse(Prev), CellKind.Int, Min: -6, Max: 6, Cells: Cells(previous), Board: new WorldStateBoard("board")),
                Slot("ownVac", 0, 65535), Slot("ownOcc", 0, 65535), Slot("otherVac", 0, 65535), Slot("otherOcc", 0, 65535),
                Slot("quiet", 0, 1), Slot("capture", 0, 1), Slot("castle", 0, 1), Slot("noChange", 0, 1),
                Slot("fromCell", -6, 16), Slot("toCell", -6, 16), Slot("capturedCell", -6, 16),
                Slot("kind", 0, 7),
            ], Lattices: [topology]),
            Rules = ClassifyRules(),
        };
    }

    private static long Kind(long[] previous, long[] current) {
        using var fixture = Fixtures.FreshServer(definition: Document(previous, current));
        fixture.Step();
        return WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "kind")!.Cells, WorldStateRow.SlotKey)!.Value;
    }

    private static readonly long[] Start = [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0];

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
}
