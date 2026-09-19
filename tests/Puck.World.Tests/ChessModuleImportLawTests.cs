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
    private static long Cell(WorldStateRow row, string key) => (row.Cells?.SingleOrDefault(predicate: c => (c.Key.Value == key))?.Value.AsInt
        ?? ((row.EffectiveDomain is StateDomain.CellsOf board)
        ? board.Empty
        : throw new InvalidOperationException(message: $"missing {row.Name}[{key}]")));
    private static WorldDefinition LoadMinimalHost() {
        var path = Path.Combine(
            RepoRoot(),
            "tests",
            "Puck.World.Tests",
            "Fixtures",
            "minimal-chess-host.world.json"
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path,
                out var definition,
                out var reason
            ),
            userMessage: reason
        );

        return definition!;
    }
    private static void MoveTo(WorldFixture fixture, WorldBody body, int file, int rank) {
        var x = (OriginX + ((file + 0.5f) * CellSize));
        var z = (OriginZ + ((rank + 0.5f) * CellSize));

        body.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: x,
            y: SpawnHeight,
            yawRadians: 0f,
            z: z
        );

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }
    }
    // A piece's body, resolved the SAME way the engine's own placement:$each does — never a literal index.
    private static WorldBody Piece(WorldFixture fixture, string placementId) {
        var placements = fixture.Server.Definition.Placements;
        var ordinal = -1;

        for (var index = 0; (index < placements.Count); index++) {
            if (string.Equals(
                a: placements[index].Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )) {
                ordinal = index;

                break;
            }
        }

        Assert.True(
            condition: (ordinal >= 0),
            userMessage: $"'{placementId}' names no declared placement"
        );

        var bodyIndex = fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal);

        Assert.True(
            condition: (bodyIndex >= 0),
            userMessage: $"'{placementId}' is not inhabited"
        );

        return fixture.Server.Body(index: bodyIndex)!;
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
    private static WorldStateRow Row(WorldFixture fixture, string name) =>
        WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: name
        )!;
    // The first stable physical position seeds accepted state. Later settles are judged
    // against lastLegal; rejected or incomplete observations never replace that baseline.
    private static void SettleFromSpawn(WorldFixture fixture) {
        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }
    }
    private static long Slot(WorldFixture fixture, string name) => Cell(
        Row(
            fixture: fixture,
            name: name
        ),
        WorldStateRow.SlotKey
    );

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void APhysicalCastleCannotCrossAKnightAttack(bool attacked) {
        var source = LoadMinimalHost();
        string[] pieces = ["piece4", "piece7", "piece28", "piece30"];
        var definition = source with {
            PlacementRowsRaw = [.. source.Placements.Where(predicate: p => ((p.Inhabit?.Kit != "piece") || pieces.Contains(value: p.Id))).Select(selector: p =>
                ((p.Id == "piece30")
            ? p with { Position = new Puck.Assets.Documents.DocumentVector3(
                    x: (attacked
                ? 0.5f
                : -0.5f),
                    y: 1.8f,
                    z: -0.3f
                ) }
            : p))],
            StateRaw = source.StateRaw! with {
                World = [.. source.State.Select(selector: r =>
                ((r.Name.Value is "pieceCode" or "pieceCell")
            ? r with { Cells = [.. r.Cells!.Where(predicate: c => pieces.Contains(value: c.Key.Value))] }
            : r))],
            },
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        SettleFromSpawn(fixture: fixture);
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "inCheck"
                ),
                "0"
            )
        );
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "castleRights"
            )
        );
        // g3 attacks f1 alone among the king's three squares; b3 is the otherwise identical safe control.
        var king = Piece(
            fixture: fixture,
            placementId: "piece4"
        );
        var rook = Piece(
            fixture: fixture,
            placementId: "piece7"
        );

        king.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + (6.5f * CellSize)),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + (0.5f * CellSize))
        );
        MoveTo(
            fixture,
            rook,
            file: 5,
            rank: 0
        );
        Assert.Equal(
            4,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        );
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "inCheck"
                ),
                "0"
            )
        );
        Assert.Equal(
            (attacked
            ? 1
            : 0),
            Cell(
                Row(
                    fixture: fixture,
                    name: "castleTransitAttacked"
                ),
                "wk"
            )
        );
        Assert.Equal(
            (attacked
            ? 0
            : 1),
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            (attacked
            ? 0
            : 1),
            Slot(
                fixture: fixture,
                name: "turn"
            )
        );
        Assert.Equal(
            (attacked
            ? 6
            : 0),
            Cell(
                Row(
                    fixture: fixture,
                    name: "lastLegal"
                ),
                "4"
            )
        );
        Assert.Equal(
            (attacked
            ? 0
            : 3),
            Slot(
                fixture: fixture,
                name: "castleRights"
            )
        ); // only accepted moves consume rights.
    }
    [Fact]
    public void CaptureRecordsMoveKindTwo() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        var blackPawn = Piece(
            fixture: fixture,
            placementId: "piece16"
        ); // a7

        MoveTo(
            fixture,
            blackPawn,
            file: 2,
            rank: 2
        ); // c3 — the initial position, before the first settle.

        var knight = Piece(
            fixture: fixture,
            placementId: "piece1"
        ); // b1

        // Remove the defender and land the attacker in the same observation. A separate
        // removal settle is also safe: accepted chess state waits for the complete capture.
        blackPawn.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + 1.7f),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + 0.5f)
        );
        SettleFromSpawn(fixture: fixture); // A removal-only observation must not lose the accepted defender.
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "turn"
            )
        );
        Assert.Equal(
            -1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "lastLegal"
                ),
                "18"
            )
        );
        MoveTo(
            fixture,
            knight,
            file: 2,
            rank: 2
        ); // c3 — a legal knight jump from b1, capturing the black pawn.

        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            2,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        );
        Assert.Equal(
            -1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "captured"
            )
        );
    }
    [Fact]
    public void E2E4RecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var pawn = Piece(
            fixture: fixture,
            placementId: "piece12"
        ); // e2

        MoveTo(
            fixture,
            pawn,
            file: 4,
            rank: 3
        ); // e4

        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            28,
            Cell(
                Row(
                    fixture: fixture,
                    name: "pieceCell"
                ),
                "piece12"
            )
        ); // e4 = rank3*8 + file4
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "28"
            )
        );
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "12"
            )
        ); // e2 vacated
    }
    [Fact]
    public void IllegalKnightMoveRecordsIllegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var knight = Piece(
            fixture: fixture,
            placementId: "piece6"
        ); // g1
        var illegalBefore = Slot(
            fixture: fixture,
            name: "illegalCount"
        );

        MoveTo(
            fixture,
            knight,
            file: 6,
            rank: 2
        ); // g3 — straight ahead, not an L-shape: illegal for a knight.

        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            (illegalBefore + 1),
            Slot(
                fixture: fixture,
                name: "illegalCount"
            )
        );

        // The rejected g3 observation never replaces the accepted g1 origin.
        MoveTo(
            fixture,
            knight,
            file: 5,
            rank: 2
        ); // g1 -> f3 is legal.
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
    }
    [Fact]
    public void KingStepRecordsLegalAndMultiStepRefused() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whiteEPawn = Piece(
            fixture: fixture,
            placementId: "piece12"
        ); // e2
        var blackEPawn = Piece(
            fixture: fixture,
            placementId: "piece20"
        ); // e7
        var whiteKing = Piece(
            fixture: fixture,
            placementId: "piece4"
        ); // e1 (cell 4, code 6)

        // Turn 1 (White): e2 -> e4
        MoveTo(
            fixture,
            whiteEPawn,
            file: 4,
            rank: 3
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );

        // Turn 2 (Black): e7 -> e5
        MoveTo(
            fixture,
            blackEPawn,
            file: 4,
            rank: 4
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );

        // Turn 3 (White): King attempts illegal multi-square slide e1 -> e3 (cell 20)
        var illegalBefore = Slot(
            fixture: fixture,
            name: "illegalCount"
        );

        MoveTo(
            fixture,
            whiteKing,
            file: 4,
            rank: 2
        ); // e3
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            (illegalBefore + 1),
            Slot(
                fixture: fixture,
                name: "illegalCount"
            )
        );

        // Control: King takes a legal single step to e2 (file 4, rank 1 = cell 12)
        MoveTo(
            fixture,
            whiteKing,
            file: 4,
            rank: 1
        ); // e2
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        );
        Assert.Equal(
            6,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "mover"
            )
        ); // king code is 6
        Assert.Equal(
            12,
            Cell(
                Row(
                    fixture: fixture,
                    name: "pieceCell"
                ),
                "piece4"
            )
        );
        Assert.Equal(
            6,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "12"
            )
        );
    }
    [Fact]
    public void PawnCaptureRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        var whitePawn = Piece(
            fixture: fixture,
            placementId: "piece12"
        ); // e2 (cell 12)
        var blackPawn = Piece(
            fixture: fixture,
            placementId: "piece19"
        ); // d7 (cell 51)

        // Seat the initial black pawn on d3 before the first accepted snapshot.
        MoveTo(
            fixture,
            blackPawn,
            file: 3,
            rank: 2
        );

        // In one settle window, black pawn is lifted to the margin and white pawn lands on d3 (diagonal capture from e2)
        blackPawn.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + 1.7f),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + 0.5f)
        );
        MoveTo(
            fixture,
            whitePawn,
            file: 3,
            rank: 2
        ); // d3 (dx=-1, dy=1 from e2: SW step)

        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            2,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        );
        Assert.Equal(
            -1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "captured"
            )
        ); // captured black pawn
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "mover"
            )
        ); // white pawn mover code
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "19"
            )
        ); // d3 holds white pawn
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "12"
            )
        ); // e2 vacated
    }
    [Fact]
    public void QueenDiagonalSlideRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        SettleFromSpawn(fixture: fixture);

        var whiteEPawn = Piece(
            fixture: fixture,
            placementId: "piece12"
        ); // e2
        var blackEPawn = Piece(
            fixture: fixture,
            placementId: "piece20"
        ); // e7
        var whiteQueen = Piece(
            fixture: fixture,
            placementId: "piece3"
        ); // d1 (cell 3, code 5)

        // Turn 1 (White): e2 -> e4
        MoveTo(
            fixture,
            whiteEPawn,
            file: 4,
            rank: 3
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "turn"
            )
        ); // Black's turn

        // Turn 2 (Black): e7 -> e5
        MoveTo(
            fixture,
            blackEPawn,
            file: 4,
            rank: 4
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "turn"
            )
        ); // White's turn

        // Turn 3 (White): Queen d1 -> h5 (file 7, rank 4 = cell 39) along opened diagonal
        MoveTo(
            fixture,
            whiteQueen,
            file: 7,
            rank: 4
        );
        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        ); // quiet move
        Assert.Equal(
            5,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "mover"
            )
        ); // queen code is 5
        Assert.Equal(
            39,
            Cell(
                Row(
                    fixture: fixture,
                    name: "pieceCell"
                ),
                "piece3"
            )
        );
        Assert.Equal(
            5,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "39"
            )
        );
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "3"
            )
        );
    }
    [Fact]
    public void RestatedTabletopSeatsPiecesAndDerivesTheirCellsThroughTheRealServer() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        Assert.Equal(
            20f,
            fixture.Server.Definition.Placements.Single(predicate: p => (p.Id == "tabletop")).Position.X
        );

        SettleFromSpawn(fixture: fixture);

        // e2 (piece12, a white pawn) settles onto cell 12 (rank 1 = z index 1, file e = x index 4 => 1*8+4=12) and
        // stamps its code onto the board — the SAME chain WorldPlacementFrameCompilation (composed spawn pose) ->
        // $physics:quiescent (settle) -> $upright (resting) -> $board:cellOf:board:placement:$each (derive) ->
        // placement:$each (write-board) rides for every piece, addressed by placement id alone.
        Assert.Equal(
            12,
            Cell(
                Row(
                    fixture: fixture,
                    name: "pieceCell"
                ),
                "piece12"
            )
        );
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "pieceCode"
                ),
                "piece12"
            )
        ); // white pawn code
        Assert.Equal(
            1,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "12"
            )
        );
    }
    [Fact]
    public void WhiteKingsideCastleRecordsLegal() {
        using var fixture = Fixtures.FreshServer(definition: LoadMinimalHost());

        var whiteBishop = Piece(
            fixture: fixture,
            placementId: "piece5"
        ); // f1
        var whiteKnight = Piece(
            fixture: fixture,
            placementId: "piece6"
        ); // g1
        var whiteKing = Piece(
            fixture: fixture,
            placementId: "piece4"
        ); // e1
        var whiteRook = Piece(
            fixture: fixture,
            placementId: "piece7"
        ); // h1

        // Configure the initial position before its first accepted settle.
        whiteBishop.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + 1.7f),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + 0.1f)
        );
        whiteKnight.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + 1.7f),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + 0.3f)
        );
        SettleFromSpawn(fixture: fixture);

        // A lifted rook may settle off-board and return without consuming legal rights.
        whiteRook.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + 1.7f),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + 0.5f)
        );
        SettleFromSpawn(fixture: fixture);
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "castleRights"
            )
        );
        Assert.Equal(
            4,
            Cell(
                Row(
                    fixture: fixture,
                    name: "lastLegal"
                ),
                "7"
            )
        );
        MoveTo(
            fixture,
            whiteRook,
            file: 7,
            rank: 0
        );
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "castleRights"
            )
        );
        Assert.Equal(
            0,
            Slot(
                fixture: fixture,
                name: "turn"
            )
        );

        // Execute Kingside Castle: King e1 -> g1 (cell 4 -> 6), Rook h1 -> f1 (cell 7 -> 5)
        whiteKing.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + (6.5f * CellSize)),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + (0.5f * CellSize))
        );
        whiteRook.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (OriginX + (5.5f * CellSize)),
            y: SpawnHeight,
            yawRadians: 0f,
            z: (OriginZ + (0.5f * CellSize))
        );
        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        Assert.Equal(
            1,
            Slot(
                fixture: fixture,
                name: "verdict"
            )
        );
        Assert.Equal(
            4,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "kind"
            )
        ); // castle kind is 4
        Assert.Equal(
            6,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "mover"
            )
        ); // king code
        Assert.Equal(
            4,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "from"
            )
        ); // king from e1 (4)
        Assert.Equal(
            6,
            Cell(
                Row(
                    fixture: fixture,
                    name: "move"
                ),
                "to"
            )
        ); // king to g1 (6)
        Assert.Equal(
            6,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "6"
            )
        ); // g1 holds king
        Assert.Equal(
            4,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "5"
            )
        ); // f1 holds rook
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "4"
            )
        ); // e1 vacated
        Assert.Equal(
            0,
            Cell(
                Row(
                    fixture: fixture,
                    name: "board"
                ),
                "7"
            )
        ); // h1 vacated
    }

    private const float CellSize = 0.2f;
    // One settle window, in simulation ticks: the module's own `settleHold` row counts 60 consecutive quiescent
    // ticks before the derive chain fires, and a posed piece has to fall from SpawnHeight and latch resting first,
    // so a window shorter than that observes no derive at all.
    private const int SettleTicks = 400;
    // Restated tabletop world origin: [20, -0.5, -12] (composed position) + the fragment's own LOCAL chessBoard
    // origin [-0.8, 1.25, -0.8] = [19.2, 0.75, -12.8] — the SAME resolution TopologyCompilation.Find(WorldDefinition,
    // string) performs at runtime; recomputed by hand here as the test's own control on the anchor math.
    private const float OriginX = 19.2f;
    private const float OriginZ = -12.8f;
    private const float SpawnHeight = 1.3f; // authored piece spawn height — pieces DROP onto the board and settle within its band.
}
