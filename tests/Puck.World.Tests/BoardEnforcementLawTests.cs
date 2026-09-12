using System.Numerics;

using Xunit;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// A tiny, chess-free tabletop: one Grid strip, one kinematic piece body, a hand-authored judge rule that refuses
/// any move onto cell 3. Proves <see cref="WorldBoardEnforcement.Return"/>/<see cref="WorldBoardEnforcement.Record"/>
/// engine-side, independent of the shipped chess module's own occupancy-diff classifier.
/// </summary>
public sealed class BoardEnforcementLawTests {
    private const string TopologyName = "strip";
    private const string OccupancyRowName = "cells";
    private const string VerdictRowName = "verdict";
    private const string MoveRowName = "move";
    private const string TurnRowName = "turn";
    private const float CellSize = 2f;
    private const int Width = 4;
    private const int ForbiddenCell = 3;

    private static float CellCentreX(int cell) => ((cell + 0.5f) * CellSize);
    private static float CellCentreZ() => (0.5f * CellSize);

    private static WorldPrototype BuildPieceCreation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.2f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "piece",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "piece");

        return new WorldPrototype(Id: "piece", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    private static WorldDefinition BuildBoardDocument(WorldBoardEnforcement enforcement) {
        var creation = BuildPieceCreation();
        var topology = new LatticeTopology.Grid(Name: TopologyName, Origin: Vector3.Zero, CellSize: CellSize, Width: Width, Depth: 1);

        var document = Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementsRaw = new WorldPlacementsSection(
                Policy: Fixtures.StandardAuthoring,
                Rows: [
                    new WorldPlacement(
                        Id: "board",
                        PrototypeId: creation.Id,
                        Position: Vector3.Zero,
                        YawDegrees: 0f,
                        Scale: 1f,
                        Board: new WorldPlacementBoard(
                            Topology: TopologyName,
                            Occupancy: OccupancyRowName,
                            Turn: TurnRowName,
                            Verdict: VerdictRowName,
                            Move: MoveRowName,
                            Enforcement: enforcement
                        )
                    ),
                    new WorldPlacement(
                        Id: "piece",
                        PrototypeId: creation.Id,
                        Position: Vector3.Zero,
                        YawDegrees: 0f,
                        Scale: 1f,
                        Inhabit: new WorldPlacementInhabit(
                            Kit: Fixtures.SeatKitName,
                            Look: null,
                            Source: IntentSource.Idle,
                            Distribution: WorldDistribution.Default
                        )
                    ),
                ]
            ),
            StateRaw = new WorldStateSection(
                World: [
                    new WorldStateRow(
                        Name: CellName.Parse(candidate: OccupancyRowName),
                        Kind: CellKind.Int,
                        Domain: new StateDomain.CellsOf(Topology: TopologyName)
                    ),
                    new WorldStateRow(
                        Name: CellName.Parse(candidate: VerdictRowName),
                        Kind: CellKind.Int,
                        Cells: [new StateCell(Key: StateRow.SlotKey, Value: 1)]
                    ),
                    new WorldStateRow(
                        Name: CellName.Parse(candidate: TurnRowName),
                        Kind: CellKind.Int,
                        Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0)]
                    ),
                    new WorldStateRow(
                        Name: CellName.Parse(candidate: MoveRowName),
                        Kind: CellKind.Int,
                        Min: -1,
                        Max: (Width - 1),
                        Cells: [
                            new StateCell(Key: CellName.Parse(candidate: "from"), Value: -1),
                            new StateCell(Key: CellName.Parse(candidate: "to"), Value: -1),
                        ]
                    ),
                ],
                Lattices: [topology]
            ),
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "board-judge"),
                    Effects: [
                        new ActionEffect.SetState(
                            State: VerdictRowName,
                            Expression: ValueExpression.Parse(text: $"(move[to] == {ForbiddenCell}) ? 0 : 1")
                        ),
                    ]
                ),
            ],
        };

        return (document with {
            PopulationRaw = (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) }),
        });
    }

    private static WorldBody PieceBody(WorldFixture fixture) {
        var ordinal = -1;
        var placements = fixture.Server.Definition.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            if (string.Equals(a: placements[index].Id, b: "piece", comparisonType: StringComparison.Ordinal)) {
                ordinal = index;

                break;
            }
        }

        Assert.True(condition: (ordinal >= 0), userMessage: "'piece' names no declared placement");

        var bodyIndex = fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal);

        Assert.True(condition: (bodyIndex >= 0), userMessage: "'piece' is not inhabited");

        return fixture.Server.Body(index: bodyIndex)!;
    }

    private static int CellOf(WorldFixture fixture, WorldBody body) {
        var topology = WorldTopologyCompilation.Find(fixture.Server.Definition, TopologyName)!;

        return (topology.TryCellOf(position: body.FixedPosition, cell: out var cell) ? cell : -1);
    }

    private static void MoveOnto(WorldFixture fixture, WorldBody body, int fromCell, int toCell) {
        body.Pose(x: CellCentreX(toCell), y: 0f, z: CellCentreZ(), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console, Row: MoveRowName, Key: "from", Value: fromCell, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console, Row: MoveRowName, Key: "to", Value: toCell, Kind: WorldDocumentWriteKind.Set
        ));
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [], AppliedTransferHighWater: null, AppliedTransferIds: [], ElapsedEngineTicks: 0,
        ForwardedBodies: [], FreshCounter: 0, InDoubtTransfers: [], IsPaused: false, NextTransferId: 1,
        PortalOccupancy: [], Retained: false, ScheduleAccumulatorTicks: 0, SeededArrivals: []
    );

    [Fact]
    public void ReturnEnforcementPosesAnIllegalMoversPieceBackOntoItsOriginCellWithinBoundedTicks() {
        using var fixture = Fixtures.FreshServer(definition: BuildBoardDocument(enforcement: WorldBoardEnforcement.Return));
        var piece = PieceBody(fixture: fixture);

        piece.Pose(x: CellCentreX(0), y: 0f, z: CellCentreZ(), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();
        Assert.Equal(expected: 0, actual: CellOf(fixture: fixture, body: piece));

        MoveOnto(fixture: fixture, body: piece, fromCell: 0, toCell: ForbiddenCell);
        fixture.Step();
        Assert.Equal(expected: 0, actual: CellOf(fixture: fixture, body: piece));

        // The edge fires exactly once — a verdict left sitting at 0 does not keep returning the piece every tick.
        for (var tick = 0; (tick < 1); tick++) {
            piece.Pose(x: CellCentreX(ForbiddenCell), y: 0f, z: CellCentreZ(), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
            fixture.Step();
        }
        Assert.Equal(expected: ForbiddenCell, actual: CellOf(fixture: fixture, body: piece));
    }

    [Fact]
    public void RecordEnforcementLeavesAnIllegalMoversPieceWhereItSettled() {
        using var fixture = Fixtures.FreshServer(definition: BuildBoardDocument(enforcement: WorldBoardEnforcement.Record));
        var piece = PieceBody(fixture: fixture);

        piece.Pose(x: CellCentreX(0), y: 0f, z: CellCentreZ(), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();

        MoveOnto(fixture: fixture, body: piece, fromCell: 0, toCell: ForbiddenCell);
        fixture.Step();
        fixture.Step();

        Assert.Equal(expected: ForbiddenCell, actual: CellOf(fixture: fixture, body: piece));
    }

    [Fact]
    public void ReturnEnforcementWithoutMoveOrVerdictRowsRefusesByName() {
        var document = BuildBoardDocument(enforcement: WorldBoardEnforcement.Record);
        var stripped = document with {
            PlacementsRaw = new WorldPlacementsSection(
                Policy: Fixtures.StandardAuthoring,
                Rows: [.. document.Placements.Select(selector: p => (p.Id == "board")
                    ? (p with { Board = (p.Board! with { Enforcement = WorldBoardEnforcement.Return, Move = null }) })
                    : p
                )]
            ),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: stripped, reason: out var reason));
        Assert.Contains(expectedSubstring: "board.enforcement", actualString: reason);
    }

    [Fact]
    public void CheckpointRoundTripsTheRememberedVerdictLatch() {
        using var fixture = Fixtures.FreshServer(definition: BuildBoardDocument(enforcement: WorldBoardEnforcement.Return));
        var piece = PieceBody(fixture: fixture);

        piece.Pose(x: CellCentreX(0), y: 0f, z: CellCentreZ(), yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();

        MoveOnto(fixture: fixture, body: piece, fromCell: 0, toCell: ForbiddenCell);
        fixture.Step();
        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var reason
        ), userMessage: reason);

        Assert.NotEmpty(collection: checkpoint!.BoardEnforcement!.Entries);

        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeReason
        ), userMessage: decodeReason);
        Assert.True(
            condition: DeepEqual.Compare(a: checkpoint, b: decoded),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
}
