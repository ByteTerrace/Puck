using System.Numerics;

using Puck.Maths;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for <c>poseCell</c>'s two moves: a teleport snaps the body to its authored roll and marks the jump,
/// and a glide moves it as continuous motion a client interpolates, keeping its attitude.</summary>
public sealed class PoseCellGlideLawTests {
    private const string Column = "column";
    private const float LayerHeight = 0.25f;

    private static WorldDefinition Document(bool glide, float rollDegrees) => (Fixtures.BuildDocument() with {
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
                    new WorldEffect.PoseCell(Key: "body:0", Topology: Column, Expression: ExpressionProgram.Parse(text: "stop"), Offset: Vector3.Zero, Glide: glide, RollDegrees: rollDegrees),
                ]
            ),
        ],
    });

    [Theory]
    [InlineData(true, 0f)]
    [InlineData(false, 0f)]
    [InlineData(false, 180f)]
    [InlineData(true, 180f)]
    public void APoseCellMovesTheBodyToItsCellAndGlidesOnlyWhenAsked(bool glide, float rollDegrees) {
        using var fixture = Fixtures.FreshServer(definition: Document(glide: glide, rollDegrees: rollDegrees));
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

        // A teleport stands the body at its authored roll (180 degrees turns its own up axis down); a glide keeps the
        // attitude the body had.
        var up = body.FixedOrientation.Rotate(vector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero));

        Assert.Equal(expected: ((!glide && (rollDegrees == 180f)) ? -1f : 1f), actual: (float)up.Y, tolerance: 1e-3f);
    }
}
