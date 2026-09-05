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
    // origin [-0.8, 1.25, -0.8] = [19.2, 0.75, -12.8] — the SAME resolution WorldTopologyCompilation.Find(WorldDefinition,
    // string) performs at runtime; recomputed by hand here as the test's own control on the anchor math.
    private const float OriginX = 19.2f;
    private const float OriginZ = -12.8f;
    private const float CellSize = 0.2f;
    private const float SpawnHeight = 1.3f; // authored piece spawn height — pieces DROP onto the board and settle within its band.

    private static void MoveTo(WorldFixture fixture, WorldBody body, int file, int rank) {
        var x = (OriginX + ((file + 0.5f) * CellSize));
        var z = (OriginZ + ((rank + 0.5f) * CellSize));

        body.Pose(x: x, y: SpawnHeight, z: z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }
    }

    [Fact]
    public void RestatedTabletopSeatsPiecesAndDerivesTheirCellsThroughTheRealServer() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        Assert.Equal(20f, fixture.Server.Definition.Placements.Single(p => p.Id == "tabletop").Position.X);

        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }

        // e2 (piece12, a white pawn) settles onto cell 12 (rank 1 = z index 1, file e = x index 4 => 1*8+4=12) and
        // stamps its code onto the board — the SAME chain WorldPlacementFrameCompilation (composed spawn pose) ->
        // $physics:quiescent (settle) -> $upright (resting) -> $board:cellOf:board:placement:$each (derive) ->
        // placement:$each (write-board) rides for every piece, addressed by placement id alone.
        Assert.Equal(12, Cell(Row(fixture, "pieceCell"), "piece12"));
        Assert.Equal(-1, Cell(Row(fixture, "pieceCode"), "piece12")); // white pawn code
        Assert.Equal(-1, Cell(Row(fixture, "board"), "12"));
    }

    [Fact]
    public void E2E4RecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }

        var pawn = Piece(fixture, "piece12"); // e2

        MoveTo(fixture, pawn, file: 4, rank: 3); // e4

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(28, Cell(Row(fixture, "pieceCell"), "piece12")); // e4 = rank3*8 + file4
        Assert.Equal(-1, Cell(Row(fixture, "board"), "28"));
        Assert.Equal(0, Cell(Row(fixture, "board"), "12")); // e2 vacated
    }

    [Fact]
    public void IllegalKnightMoveRecordsIllegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }

        var knight = Piece(fixture, "piece6"); // g1
        var illegalBefore = Slot(fixture, "illegalCount");

        MoveTo(fixture, knight, file: 6, rank: 2); // g3 — straight ahead, not an L-shape: illegal for a knight.

        Assert.Equal(0, Slot(fixture, "verdict"));
        Assert.Equal((illegalBefore + 1), Slot(fixture, "illegalCount"));

        // Control: the SAME knight making an ACTUALLY legal jump (g1-f3, a genuine L-shape) records legal — proving
        // the illegal verdict above discriminates a real geometry failure rather than the knight always losing.
        MoveTo(fixture, knight, file: 5, rank: 2); // f3 — legal knight jump from g1.
        Assert.Equal(1, Slot(fixture, "verdict"));
    }

    [Fact]
    public void CaptureRecordsMoveKindTwo() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        for (var tick = 0; (tick < 3000); tick++) {
            fixture.Step();
        }

        var blackPawn = Piece(fixture, "piece16"); // a7

        MoveTo(fixture, blackPawn, file: 2, rank: 2); // c3 — an arbitrary reposition, setting up the capture below.

        var knight = Piece(fixture, "piece1"); // b1

        MoveTo(fixture, knight, file: 2, rank: 2); // c3 — a legal knight jump from b1 landing on the black pawn.

        Assert.Equal(1, Slot(fixture, "verdict"));
        Assert.Equal(2, Cell(Row(fixture, "move"), "kind"));
        Assert.Equal(1, Cell(Row(fixture, "move"), "captured"));
    }
}
