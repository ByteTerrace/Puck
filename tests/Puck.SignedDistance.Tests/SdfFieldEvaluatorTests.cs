using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Puck.SignedDistance.Queries.Debug;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfFieldEvaluatorTests {
    private static SdfFieldEvaluator BuildRoundedRectangleEvaluator() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.RoundedRectangle(
            halfWidth: 1f,
            halfHeight: 0.5f,
            cornerRadius: 0.1f,
            lift: SdfLift.Extrude,
            liftAmount: 0.25f,
            material: material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));

    [Fact]
    public void DriftShellExcludesBothComparisonChannels() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var baked = new BakedWorldQuery(artifact: WorldQueryBaker.Bake(
            minX: -1f,
            minZ: -1f,
            maxX: 1f,
            maxZ: 1f,
            terrain: [],
            blockers: []
        ));
        var histogram = WorldQueryDriftInstrument.Evaluate(
            evaluator: evaluator,
            points: [Position(x: 0.0, y: 0.0, z: 0.0),],
            epsilonShell: FixedQ4816.FromDouble(value: 0.01),
            gpuInsideOrNear: static _ => true,
            baked: baked,
            groundProbeUp: FixedQ4816.One,
            groundProbeDown: FixedQ4816.One,
            bakedTolerance: FixedQ4816.One
        );

        Assert.Equal(
            expected: 1,
            actual: histogram.ExcludedByEpsilonShell
        );
        Assert.Equal(
            expected: 0,
            actual: histogram.GpuComparisons
        );
        Assert.Equal(
            expected: 0,
            actual: histogram.BakedComparisons
        );
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, -0.25)]
    [InlineData(2.0, 0.0, 0.0, 1.0)]
    public void RoundedRectangleUsesTheShaderExactDistanceBody(double x, double y, double z, double expectedDistance) {
        var evaluator = BuildRoundedRectangleEvaluator();

        var found = evaluator.TryDistance(
            position: Position(
                x: x,
                y: y,
                z: z
            ),
            distance: out var distance,
            material: out _
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: expectedDistance),
            actual: distance
        );
    }

    [Fact]
    public void RoundedRectangleSupportsTheRevolveLift() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.RoundedRectangle(
            halfWidth: 1f,
            halfHeight: 0.5f,
            cornerRadius: 0.1f,
            lift: SdfLift.Revolve,
            liftAmount: 2f,
            material: material
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var found = evaluator.TryDistance(
            position: Position(
                x: 3.0,
                y: 0.0,
                z: 0.0
            ),
            distance: out var distance,
            material: out _
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: distance
        );
    }
}
