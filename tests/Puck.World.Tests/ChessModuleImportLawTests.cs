using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>games/chess.world.json</c> is a self-contained, placement-addressed module any host can import and
/// position with one restated placement — never a body index. Loads <c>Fixtures/minimal-chess-host.world.json</c>,
/// a MINIMAL host (standard.basis plus the substrate
/// sections the garden itself authors — bodies, channels, collision, simulation, views; the <c>piece</c> kit lives
/// inside the fragment) that imports the chess fragment and restates <c>tabletop</c> at <c>[20, -0.5, -12]</c> — a
/// different position than the garden's own <c>[-8, -0.5, 12]</c> — through an ordinary keyed-row refine (the
/// importing file's own body overriding one field of the imported placement, the rest inherited unchanged). Every
/// check below resolves a piece's body through <see cref="WorldPopulation.BodyForPlacementOrdinal"/> — the SAME
/// ordinal table the engine's own <c>placement:$each</c> resolution rides — never a literal body index.
/// </summary>
public sealed class ChessModuleImportLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static WorldDefinition LoadMinimalHost() {
        var path = Path.Combine(RepoRoot(), "tests", "Puck.World.Tests", "Fixtures", "minimal-chess-host.world.json");

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason), reason);

        return definition!;
    }

    // A piece's body, resolved the SAME way the engine's own placement:$each does — never a literal index.
    private static WorldBody Piece(WorldFixture fixture, string placementId) {
        var placements = fixture.Server.Definition.Placements;
        var ordinal = -1;

        for (var index = 0; (index < placements.Count); index++) {
            if (string.Equals(a: placements[index].Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                ordinal = index;

                break;
            }
        }

        Assert.True(ordinal >= 0, $"'{placementId}' names no declared placement");

        var bodyIndex = fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal);

        Assert.True(bodyIndex >= 0, $"'{placementId}' is not inhabited");

        return fixture.Server.Body(index: bodyIndex)!;
    }

    private static WorldStateRow Row(WorldFixture fixture, string name) =>
        WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name)!;
    private static long Cell(WorldStateRow row, string key) => row.Cells!.Single(predicate: c => (c.Key.Value == key)).Value;
    private static long Slot(WorldFixture fixture, string name) => Cell(Row(fixture, name), WorldStateRow.SlotKey);

    // Restated tabletop world origin: [20, -0.5, -12] (composed position) + the fragment's own LOCAL chessBoard
    // origin [-0.8, 1.25, -0.8] = [19.2, 0.75, -12.8] — the SAME resolution TopologyCompilation.Find(WorldDefinition,
    // string) performs at runtime; recomputed by hand here as the test's own control on the anchor math.
    private const float OriginX = 19.2f;
    private const float OriginZ = -12.8f;
    private const float CellSize = 0.2f;
    private const float SpawnHeight = 1.3f; // authored piece spawn height — pieces DROP onto the board and settle within its band.

    private static void MoveTo(WorldFixture fixture, WorldBody body, int file, int rank) {
        var x = (OriginX + ((file + 0.5f) * CellSize));
        var z = (OriginZ + ((rank + 0.5f) * CellSize));

        body.Pose(x: x, y: SpawnHeight, z: z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < 400); tick++) {
            fixture.Step();
        }
    }

    // 32 tightly-packed rigid pieces settling FROM SPAWN — never a real move — can cross $physics:quiescent's edge
    // more than once before every piece has finished its own physical settle (a body still mid-shove reads resting
    // for a moment, opens the population-wide gate, then keeps moving). The document itself debounces this: a
    // `settleHold` counter (`tabletop-settle-hold`) only reaches its margin once quiescence has held continuously,
    // and every tabletop rule gates on THAT rather than the raw edge, so a mid-settle wobble never opens the
    // classifier at all. The first genuine settle (`gameStarted == 0`) is further routed through
    // `tabletop-game-start`, which snapshots `previousBoard` from the just-derived `board` before any classifier
    // reads it — the SAME reasoning that makes every later move's own `tabletop-board-snapshot` correct, applied
    // once at boot instead of assuming `previousBoard`'s all-zero row default already matches an empty board. No
    // row here is written by hand: this is real settle time, nothing else.
    private static void SettleFromSpawn(WorldFixture fixture) {
        for (var tick = 0; (tick < 400); tick++) {
            fixture.Step();
        }
    }

    [Fact]
    public void RestatedTabletopSeatsPiecesAndDerivesTheirCellsThroughTheRealServer() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        Assert.Equal(20f, fixture.Server.Definition.Placements.Single(p => p.Id == "tabletop").Position.X);

        SettleFromSpawn(fixture: fixture);

        // e2 (piece12, a white pawn) settles onto cell 12 (rank 1 = z index 1, file e = x index 4 => 1*8+4=12) and
        // stamps its code onto the board — the SAME chain WorldPlacementFrameCompilation (composed spawn pose) ->
        // $physics:quiescent (settle) -> $upright (resting) -> $board:cellOf:board:placement:$each (derive) ->
        // placement:$each (write-board) rides for every piece, addressed by placement id alone.
        Assert.Equal(12, Cell(Row(fixture, "pieceCell"), "piece12"));
        Assert.Equal(1, Cell(Row(fixture, "pieceCode"), "piece12")); // white pawn code
        Assert.Equal(1, Cell(Row(fixture, "board"), "12"));
    }

    [Fact]
    public void E2E4RecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var pawn = Piece(fixture, "piece12"); // e2

        MoveTo(fixture, pawn, file: 4, rank: 3); // e4

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(28, Cell(Row(fixture, "pieceCell"), "piece12")); // e4 = rank3*8 + file4
        Assert.Equal(1, Cell(Row(fixture, "board"), "28"));
        Assert.Equal(0, Cell(Row(fixture, "board"), "12")); // e2 vacated
    }

    [Fact]
    public void IllegalKnightMoveRecordsIllegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var knight = Piece(fixture, "piece6"); // g1
        var illegalBefore = Slot(fixture, "illegalCount");

        MoveTo(fixture, knight, file: 6, rank: 2); // g3 — straight ahead, not an L-shape: illegal for a knight.

        Assert.Equal(0, Slot(fixture, "verdict"));
        Assert.Equal((illegalBefore + 1), Slot(fixture, "illegalCount"));

        // Control: the SAME knight making an ACTUALLY legal jump records legal — proving the illegal verdict above
        // discriminates a real geometry failure rather than the knight always losing. Illegal moves are recorded,
        // never undone (the world never repositions a physical piece), so the knight PHYSICALLY sits at g3 (where
        // the refused move above left it) — the next jump is FROM g3, not g1.
        MoveTo(fixture, knight, file: 4, rank: 3); // e4 (dx=-2,dz=1 from g3) — a genuine L-shape onto an empty square.
        Assert.Equal(1, Slot(fixture, "verdict"));
    }

    [Fact]
    public void CaptureRecordsMoveKindTwo() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var blackPawn = Piece(fixture, "piece16"); // a7

        MoveTo(fixture, blackPawn, file: 2, rank: 2); // c3 — an arbitrary reposition, setting up the capture below.

        var knight = Piece(fixture, "piece1"); // b1

        // A capture is ONE physical action, not two: the defender is lifted clear of the board (onto the tabletop's
        // own margin beyond the 8x8 grid — $board:cellOf reads no cell there, so it stops contributing to `board`
        // at all) in the SAME settle window the attacker lands in c3's now-vacated space. Posing both bodies before
        // stepping means the population never re-quiesces in between, so the classifier sees one settle: black's
        // own vacate at c3 with no matching occupy (a piece removed), white's vacate at b1 paired with occupy at
        // c3 — the capture shape. Settling them separately would record two ordinary quiet moves instead, and
        // dropping the knight directly onto the still-resting pawn leaves both bodies permanently interpenetrating,
        // never quiescent again.
        blackPawn.Pose(x: (OriginX + 1.7f), y: SpawnHeight, z: (OriginZ + 0.5f), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        MoveTo(fixture, knight, file: 2, rank: 2); // c3 — a legal knight jump from b1, capturing the black pawn.

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(2, Cell(Row(fixture, "move"), "kind"));
        Assert.Equal(-1, Cell(Row(fixture, "move"), "captured"));
    }

    [Fact]
    public void PawnCaptureRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whitePawn = Piece(fixture, "piece12"); // e2 (cell 12)
        var blackPawn = Piece(fixture, "piece19"); // d7 (cell 51)

        // Reposition black pawn to d3 (file 3, rank 2 = cell 19) while it's white's turn (an illegal settle, so turn stays 0)
        MoveTo(fixture, blackPawn, file: 3, rank: 2);

        // In one settle window, black pawn is lifted to the margin and white pawn lands on d3 (diagonal capture from e2)
        blackPawn.Pose(x: (OriginX + 1.7f), y: SpawnHeight, z: (OriginZ + 0.5f), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        MoveTo(fixture, whitePawn, file: 3, rank: 2); // d3 (dx=-1, dy=1 from e2: SW step)

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(2, Cell(Row(fixture, "move"), "kind"));
        Assert.Equal(-1, Cell(Row(fixture, "move"), "captured")); // captured black pawn
        Assert.Equal(1, Cell(Row(fixture, "move"), "mover")); // white pawn mover code
        Assert.Equal(1, Cell(Row(fixture, "board"), "19")); // d3 holds white pawn
        Assert.Equal(0, Cell(Row(fixture, "board"), "12")); // e2 vacated
    }

    [Fact]
    public void QueenDiagonalSlideRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whiteEPawn = Piece(fixture, "piece12"); // e2
        var blackEPawn = Piece(fixture, "piece20"); // e7
        var whiteQueen = Piece(fixture, "piece3"); // d1 (cell 3, code 5)

        // Turn 1 (White): e2 -> e4
        MoveTo(fixture, whiteEPawn, file: 4, rank: 3);
        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(1, Slot(fixture, "turn")); // Black's turn

        // Turn 2 (Black): e7 -> e5
        MoveTo(fixture, blackEPawn, file: 4, rank: 4);
        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(0, Slot(fixture, "turn")); // White's turn

        // Turn 3 (White): Queen d1 -> h5 (file 7, rank 4 = cell 39) along opened diagonal
        MoveTo(fixture, whiteQueen, file: 7, rank: 4);
        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(1, Cell(Row(fixture, "move"), "kind")); // quiet move
        Assert.Equal(5, Cell(Row(fixture, "move"), "mover")); // queen code is 5
        Assert.Equal(39, Cell(Row(fixture, "pieceCell"), "piece3"));
        Assert.Equal(5, Cell(Row(fixture, "board"), "39"));
        Assert.Equal(0, Cell(Row(fixture, "board"), "3"));
    }

    [Fact]
    public void KingStepRecordsLegalAndMultiStepRefused() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whiteEPawn = Piece(fixture, "piece12"); // e2
        var blackEPawn = Piece(fixture, "piece20"); // e7
        var whiteKing = Piece(fixture, "piece4"); // e1 (cell 4, code 6)

        // Turn 1 (White): e2 -> e4
        MoveTo(fixture, whiteEPawn, file: 4, rank: 3);
        Assert.Equal(1, Slot(fixture, "verdict"));

        // Turn 2 (Black): e7 -> e5
        MoveTo(fixture, blackEPawn, file: 4, rank: 4);
        Assert.Equal(1, Slot(fixture, "verdict"));

        // Turn 3 (White): King attempts illegal multi-square slide e1 -> e3 (cell 20)
        var illegalBefore = Slot(fixture, "illegalCount");
        MoveTo(fixture, whiteKing, file: 4, rank: 2); // e3
        Assert.Equal(0, Slot(fixture, "verdict"));
        Assert.Equal(illegalBefore + 1, Slot(fixture, "illegalCount"));

        // Control: King takes a legal single step to e2 (file 4, rank 1 = cell 12)
        MoveTo(fixture, whiteKing, file: 4, rank: 1); // e2
        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(1, Cell(Row(fixture, "move"), "kind"));
        Assert.Equal(6, Cell(Row(fixture, "move"), "mover")); // king code is 6
        Assert.Equal(12, Cell(Row(fixture, "pieceCell"), "piece4"));
        Assert.Equal(6, Cell(Row(fixture, "board"), "12"));
    }

    [Fact]
    public void WhiteKingsideCastleRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whiteBishop = Piece(fixture, "piece5"); // f1
        var whiteKnight = Piece(fixture, "piece6"); // g1
        var whiteKing = Piece(fixture, "piece4"); // e1
        var whiteRook = Piece(fixture, "piece7"); // h1

        // Clear transit squares f1 and g1 by moving bishop and knight off-board onto margin
        whiteBishop.Pose(x: (OriginX + 1.7f), y: SpawnHeight, z: (OriginZ + 0.1f), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        MoveTo(fixture, whiteKnight, file: 6, rank: 2); // off transit square
        // In the next settle window, move knight off-board as well
        whiteKnight.Pose(x: (OriginX + 1.7f), y: SpawnHeight, z: (OriginZ + 0.3f), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        // Reposition King back to e1 and Rook back to h1 to ensure starting home cells before castling
        whiteKing.Pose(x: (OriginX + 4.5f * CellSize), y: SpawnHeight, z: (OriginZ + 0.5f * CellSize), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        whiteRook.Pose(x: (OriginX + 7.5f * CellSize), y: SpawnHeight, z: (OriginZ + 0.5f * CellSize), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        // Execute Kingside Castle: King e1 -> g1 (cell 4 -> 6), Rook h1 -> f1 (cell 7 -> 5)
        whiteKing.Pose(x: (OriginX + 6.5f * CellSize), y: SpawnHeight, z: (OriginZ + 0.5f * CellSize), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        whiteRook.Pose(x: (OriginX + 5.5f * CellSize), y: SpawnHeight, z: (OriginZ + 0.5f * CellSize), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(4, Cell(Row(fixture, "move"), "kind")); // castle kind is 4
        Assert.Equal(6, Cell(Row(fixture, "move"), "mover")); // king code
        Assert.Equal(4, Cell(Row(fixture, "move"), "from")); // king from e1 (4)
        Assert.Equal(6, Cell(Row(fixture, "move"), "to")); // king to g1 (6)
        Assert.Equal(6, Cell(Row(fixture, "board"), "6")); // g1 holds king
        Assert.Equal(4, Cell(Row(fixture, "board"), "5")); // f1 holds rook
        Assert.Equal(0, Cell(Row(fixture, "board"), "4")); // e1 vacated
        Assert.Equal(0, Cell(Row(fixture, "board"), "7")); // h1 vacated
    }
}
