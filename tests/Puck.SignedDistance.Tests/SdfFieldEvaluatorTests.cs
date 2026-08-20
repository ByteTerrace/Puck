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

    /// <summary>The ground-height bake builds one terrain rectangle and runs one ground march per cell before handing
    /// the region to <see cref="WorldQueryBaker"/>, so the cell budget has to be measured first or the refusal costs
    /// the whole working set it exists to prevent. The refused region below is 16000x16000 cells: if the per-cell loop
    /// ran, the refusal would arrive a minute-plus later, after every one of those marches.</summary>
    [Fact]
    public void GroundHeightBakeRefusesAnOverBudgetRegionBeforeMarchingIt() {
        var evaluator = BuildRoundedRectangleEvaluator();
        var refusal = Assert.Throws<ArgumentException>(testCode: () => WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxX: 2000f,
            maxZ: 2000f,
            minX: -2000f,
            minZ: -2000f,
            probeDown: 4f,
            probeUp: 4f
        ));

        Assert.Equal(
            expected: "maxCellCount",
            actual: refusal.ParamName
        );

        // The ceiling is now the caller's to raise, and a region inside it still bakes.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxCellCount: 0,
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            probeDown: 4f,
            probeUp: 4f
        ));

        var control = WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxCellCount: 16,
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            probeDown: 4f,
            probeUp: 4f
        );

        Assert.Equal(
            expected: 4,
            actual: control.Width
        );
        Assert.Equal(
            expected: 4,
            actual: control.Height
        );
    }

    [Fact]
    public void GroundHeightBakeWalksLargeCoordinatesByCellIndex() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material
        );

        const float MinX = 8_388_608f; // 2^23: adding the baker's 0.25f cell size to this float makes no progress.
        var artifact = WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: new SdfFieldEvaluator(program: builder.Build()),
            maxCellCount: 4,
            maxX: (MinX + 1f),
            maxZ: 0.25f,
            minX: MinX,
            minZ: 0f,
            probeDown: 1f,
            probeUp: 1f
        );

        Assert.Equal(expected: 4, actual: artifact.Width);
        Assert.Equal(expected: 1, actual: artifact.Height);
        Assert.Equal(expected: 4, actual: artifact.HeightRaw.Length);
        Assert.All(
            action: height => Assert.Equal(expected: 0L, actual: height),
            collection: artifact.HeightRaw.ToArray()
        );
    }
}
