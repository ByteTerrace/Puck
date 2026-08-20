using System.Diagnostics;
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

    /// <summary>The ground-height bake allocates one height cell and runs one ground march per cell of the measured
    /// grid, so the cell budget has to be measured first or the refusal costs the whole working set it exists to
    /// prevent. The refused region below is 16000x16000 cells: if the per-cell loop ran, the refusal would arrive a
    /// minute-plus later, after every one of those 256 million marches — which is what the elapsed bound pins.</summary>
    [Fact]
    public void GroundHeightBakeRefusesAnOverBudgetRegionBeforeMarchingIt() {
        var evaluator = BuildRoundedRectangleEvaluator();
        var elapsed = Stopwatch.StartNew();
        var refusal = Assert.Throws<ArgumentException>(testCode: () => WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxX: 2000f,
            maxZ: 2000f,
            minX: -2000f,
            minZ: -2000f,
            probeDown: 4f,
            probeUp: 4f
        ));

        elapsed.Stop();

        Assert.Equal(
            expected: "maxCellCount",
            actual: refusal.ParamName
        );
        Assert.True(
            condition: (elapsed.Elapsed < TimeSpan.FromSeconds(value: 10.0)),
            userMessage: $"The over-budget refusal took {elapsed.Elapsed}, long enough to have marched cells before refusing."
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

    /// <summary>The bake authors one float point per cell, so a region whose coordinates are coarser in
    /// <see cref="float"/> than the baker's cell size cannot address one cell apart from the next. It is refused by
    /// name rather than answered with an artifact whose cells silently hold a neighbour's ground or none at all — and
    /// the refusal is reached by measuring the grid, never by walking it, so it costs no marches.</summary>
    [Fact]
    public void GroundHeightBakeRefusesAGridFloatCannotAddressCellWise() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // A SLOPED ground, pitched 1:4 along X and 1:2 along Z and translated out to the addressable grid's own
        // corner, so every cell centre has a DIFFERENT ground height. A flat plane grounds every column at zero and
        // cannot tell a walk that reached the authored cells from one that marched somewhere else entirely.
        const float Addressable = 131_072f;

        _ = builder
            .Translate(offset: new Vector3(
            x: Addressable,
            y: 0f,
            z: Addressable
        ))
            .Plane(
            normal: new Vector3(
                x: 1f,
                y: 4f,
                z: 2f
            ),
            offset: 0f,
            material: material
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        const float Unaddressable = 8_388_608f; // 2^23: one float step there is 1.0, four times the 0.25 cell size.
        var refusal = Assert.Throws<ArgumentException>(testCode: () => WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxCellCount: 4,
            maxX: (Unaddressable + 1f),
            maxZ: 0.25f,
            minX: Unaddressable,
            minZ: 0f,
            probeDown: 1f,
            probeUp: 1f
        ));

        Assert.Equal(
            expected: "maxX",
            actual: refusal.ParamName
        );

        // The same grid shape far from the origin but still float-addressable bakes, and its cells carry the ground
        // under their OWN centre rather than the sentinel a mis-addressed sample would leave behind. Each expected
        // height is the slope read at that cell's centre offset from the grid corner: -(u/4 + v/2) for a centre
        // (u, v) cell-local to the translated plane, so dropping either origin term from the walk moves every probe
        // to a column where this plane is kilometres away and the bake answers with the sentinel instead.
        var walk = Stopwatch.StartNew();
        var artifact = WorldQueryDriftInstrument.BakeGroundHeightArtifact(
            evaluator: evaluator,
            maxCellCount: 8,
            maxX: (Addressable + 1f),
            maxZ: (Addressable + 0.5f),
            minX: Addressable,
            minZ: Addressable,
            probeDown: 1f,
            probeUp: 1f
        );

        walk.Stop();

        // The bounded-work leg: a walk that steps float bounds instead of integer cell indices does not answer
        // wrongly, it never answers at all, and an unbounded loop appending cells reports as a stalled run.
        Assert.True(
            condition: (walk.Elapsed < TimeSpan.FromSeconds(value: 10.0)),
            userMessage: $"The eight-cell bake took {walk.Elapsed}, which is not a bounded cell walk."
        );
        Assert.Equal(expected: 4, actual: artifact.Width);
        Assert.Equal(expected: 2, actual: artifact.Height);
        Assert.Equal(expected: 8, actual: artifact.HeightRaw.Length);

        double[] expected = [
            -0.09375, -0.15625, -0.21875, -0.28125,
            -0.21875, -0.28125, -0.34375, -0.40625,
        ];

        for (var cell = 0; (cell < expected.Length); cell++) {
            Assert.Equal(
                expected: expected[cell],
                actual: ((double)FixedQ4816.FromRawBits(value: artifact.HeightRaw[cell])),
                tolerance: 0.005
            );
        }
    }
}
