using System.Numerics;

using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for <c>poseCell</c>'s two moves: a teleport snaps the body and marks the jump, and a glide moves it as
/// continuous motion a client interpolates, keeping its attitude.</summary>
public sealed class PoseCellGlideLawTests {
    private const string Column = "column";
    private const float LayerHeight = 0.25f;

    private static WorldDefinition Document(bool glide) => (Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
            World: [
                new WorldStateRow(
                    Name: CellName.Parse(candidate: "stop"),
                    Kind: CellKind.Int,
                    Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 3L))]
                ),
            ],
            Lattices: [new LatticeTopology.Box(Name: Column, Origin: new Vector3(x: 4f, y: 0f, z: 4f), CellSize: 2f, Width: 1, Depth: 1, Layers: 8, LayerHeight: LayerHeight)]
        ),
        Rules = [
            new WorldRule(
                Name: CellName.Parse(candidate: "ride"),
                Effects: [
                    new WorldEffect.PoseCell(Key: "body:0", Topology: Column, Expression: ExpressionProgram.Parse(text: "stop"), Offset: Vector3.Zero, Glide: glide),
                ]
            ),
        ],
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APoseCellMovesTheBodyToItsCellAndGlidesOnlyWhenAsked(bool glide) {
        using var fixture = Fixtures.FreshServer(definition: Document(glide: glide));
        var body = fixture.JoinSeat();

        body.Pose(x: 0f, y: 0f, z: 0f, yawRadians: 1f, pitchRadians: 0f, rollRadians: 0f);
        _ = body.TakeContinuity();
        fixture.Step();

        Assert.Equal(expected: 5f, actual: (float)body.FixedPosition.X, tolerance: 1e-4f);
        Assert.Equal(expected: (3 * LayerHeight), actual: (float)body.FixedPosition.Y, tolerance: 0.05f);
        Assert.Equal(expected: 5f, actual: (float)body.FixedPosition.Z, tolerance: 1e-4f);
        Assert.Equal(
            expected: (glide ? EntityContinuityKind.Continuous : EntityContinuityKind.Teleport),
            actual: body.PeekContinuity().Kind
        );
    }
}
