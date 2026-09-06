using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The contracts an <see cref="SdfDistanceGrid"/> and the <see cref="SdfBandedFieldEvaluator"/> over it carry against
/// the exact <see cref="SdfFieldEvaluator"/> they are baked from: a corner bound never exceeds the exact field, the
/// banded surface is bit-identical to the exact one inside its band and for every gradient and overlap, a march that
/// stays inside the band is bit-identical, and the bake is a pure function of the program.
/// </summary>
public sealed class SdfDistanceGridLawTests {
    private const double CellSize = 0.5;
    private const double ContactReach = 0.4;
    private const double Padding = 4.0;

    private static readonly FixedVector3 Down = new(
        X: FixedQ4816.Zero,
        Y: -FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3[] Directions = [
        Vector(x: 1.0, y: 0.0, z: 0.0),
        Vector(x: -1.0, y: 0.0, z: 0.0),
        Vector(x: 0.0, y: 1.0, z: 0.0),
        Vector(x: 0.0, y: -1.0, z: 0.0),
        Vector(x: 0.0, y: 0.0, z: 1.0),
        Vector(x: 0.0, y: 0.0, z: -1.0),
        Vector(x: 1.0, y: -0.5, z: 1.0),
        Vector(x: -1.0, y: -0.25, z: 0.5),
    ];

    // A floor plane (finite bound impossible, so it never sizes the grid) under a row of hard-union instances, one
    // smooth-union instance, and one scoped instance carving a box out of a sphere — every compose family the exact
    // evaluator treats differently, so the bound and the band are proven across all of them.
    private static SdfProgram BuildProgram() {
        var builder = new SdfProgramBuilder();
        var floor = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var solid = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        var soft = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

        _ = builder.Plane(
            material: floor,
            normal: Vector3.UnitY,
            offset: 0f
        );

        for (var index = 0; (index < 4); index++) {
            var center = new Vector3((-6f + (index * 4f)), 0.75f, 0f);

            _ = builder.Instance(
                boundCenter: center,
                boundRadius: 1.4f,
                emit: b => {
                    _ = b.ResetPoint();
                    _ = b.Translate(offset: center);
                    _ = b.Box(
                        halfExtents: new Vector3(0.75f, 0.75f, 0.75f),
                        material: solid,
                        round: 0.05f
                    );
                }
            );
        }

        var smoothCenter = new Vector3(0f, 1f, 5f);

        _ = builder.Instance(
            boundCenter: smoothCenter,
            boundRadius: 2.5f,
            emit: b => {
                _ = b.ResetPoint();
                _ = b.Translate(offset: smoothCenter);
                _ = b.Sphere(
                    blend: SdfBlendOp.SmoothUnion,
                    material: soft,
                    radius: 1f,
                    smooth: 0.5f
                );
            }
        );

        var carvedCenter = new Vector3(0f, 1.5f, -5f);

        _ = builder.Instance(
            boundCenter: carvedCenter,
            boundRadius: 2.5f,
            emit: b => {
                _ = b.PushField(compose: SdfBlendOp.Union);
                _ = b.ResetPoint();
                _ = b.Translate(offset: carvedCenter);
                _ = b.Sphere(
                    material: solid,
                    radius: 1.5f
                );
                _ = b.Box(
                    blend: SdfBlendOp.Subtraction,
                    halfExtents: new Vector3(0.5f, 2f, 0.5f),
                    material: solid,
                    round: 0f
                );
                _ = b.PopField();
            }
        );
        _ = builder.ResetPoint();

        return builder.Build(buildInstanceGrid: false);
    }
    private static (SdfFieldEvaluator Exact, SdfDistanceGrid Grid, SdfBandedFieldEvaluator Banded) BuildFixture() {
        var exact = new SdfFieldEvaluator(program: BuildProgram());
        var program = BuildProgram();
        var grid = SdfDistanceGrid.TryCover(
            cellSize: FixedQ4816.FromDouble(value: CellSize),
            exact: exact,
            padding: FixedQ4816.FromDouble(value: Padding),
            program: program
        );

        Assert.NotNull(@object: grid);

        return (exact, grid, new SdfBandedFieldEvaluator(
            contactReach: FixedQ4816.FromDouble(value: ContactReach),
            exact: exact,
            grid: grid
        ));
    }
    private static FixedQ4816 Exact(SdfFieldEvaluator evaluator, FixedPosition position) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: position
        ));

        return distance;
    }
    // The lattice the laws sample: every 0.3 units over the covered box, deliberately off the grid's own corners.
    private static IEnumerable<FixedPosition> Lattice() {
        for (var x = -9.1; (x <= 9.1); x += 0.3) {
            for (var y = -0.7; (y <= 4.1); y += 0.3) {
                for (var z = -8.3; (z <= 8.3); z += 0.3) {
                    yield return Position(
                        x: x,
                        y: y,
                        z: z
                    );
                }
            }
        }
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: Vector(
            x: x,
            y: y,
            z: z
        ));
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

    [Fact]
    public void TheBoundNeverExceedsTheExactFieldAndCoversEveryFiniteInstance() {
        var (exact, grid, _) = BuildFixture();
        var bounded = 0;

        foreach (var position in Lattice()) {
            Assert.True(condition: position.TryDelta(
                delta: out var world,
                origin: FixedPosition.Zero
            ));

            if (!grid.TryLowerBound(
                lowerBound: out var lowerBound,
                material: out _,
                world: in world
            )) {
                continue;
            }

            bounded++;

            var field = Exact(
                evaluator: exact,
                position: position
            );

            Assert.True(
                condition: (lowerBound <= field),
                userMessage: $"the grid claimed {lowerBound} at {world} where the exact field reads {field}"
            );
        }

        Assert.True(condition: (bounded > 10_000), userMessage: $"the lattice touched only {bounded} bounded points");

        // The outermost instance bounds sit at x = ±7.4 (the box row), z = ±7.5 (the smooth and carved instances),
        // y from -0.65 to 4; the grid starts at least the padding beyond each and snaps outward to a corner.
        Assert.True(condition: (((double)grid.Origin.X) <= (-7.4 - Padding)));
        Assert.True(condition: (((double)grid.Origin.Z) <= (-7.5 - Padding)));
        Assert.True(condition: (((double)grid.CornerPosition(x: (grid.CornerCountX - 1), y: 0, z: 0).X) >= (7.4 + Padding)));
        Assert.True(condition: (((double)grid.CornerPosition(x: 0, y: 0, z: (grid.CornerCountZ - 1)).Z) >= (7.5 + Padding)));
        Assert.Equal(expected: CellSize, actual: ((double)grid.CellSize));
    }

    [Fact]
    public void InsideTheBandTheBandedSurfaceIsTheExactOne() {
        var (exact, _, banded) = BuildFixture();
        var inBand = 0;
        var beyond = 0;

        foreach (var position in Lattice()) {
            var field = Exact(
                evaluator: exact,
                position: position
            );

            Assert.True(condition: exact.TryDistance(
                distance: out var exactDistance,
                material: out var exactMaterial,
                position: position
            ));
            Assert.True(condition: banded.TryDistance(
                distance: out var bandedDistance,
                material: out var bandedMaterial,
                position: position
            ));

            if (field < banded.Band) {
                inBand++;
                Assert.Equal(expected: exactDistance.Value, actual: bandedDistance.Value);
                Assert.Equal(expected: exactMaterial, actual: bandedMaterial);
            } else {
                beyond++;
                Assert.True(condition: (bandedDistance <= exactDistance), userMessage: $"beyond the band at {position.Local}: banded {bandedDistance} exceeds exact {exactDistance}");
                Assert.True(condition: (bandedDistance >= banded.Band), userMessage: $"beyond the band at {position.Local}: banded {bandedDistance} fell under the band {banded.Band}");
            }

            var exactGradient = exact.TryFieldGradient(
                gradient: out var expectedGradient,
                position: position
            );

            Assert.Equal(expected: exactGradient, actual: banded.TryFieldGradient(
                gradient: out var actualGradient,
                position: position
            ));
            Assert.Equal(expected: expectedGradient, actual: actualGradient);

            foreach (var radius in new[] { 0.0, 0.2, 0.4, 1.0 }) {
                var fixedRadius = FixedQ4816.FromDouble(value: radius);

                Assert.Equal(expected: exact.Overlap(center: position, radius: fixedRadius), actual: banded.Overlap(center: position, radius: fixedRadius));
            }
        }

        Assert.True(condition: (inBand > 1_000), userMessage: $"only {inBand} lattice points fell inside the band");
        Assert.True(condition: (beyond > 1_000), userMessage: $"only {beyond} lattice points fell beyond the band");
    }

    [Fact]
    public void CastsAgreeOnHitOrMissAndABandBoundMarchIsBitIdentical() {
        var (exact, grid, banded) = BuildFixture();
        var maxDistance = FixedQ4816.FromInteger(value: 6L);
        var casts = 0;
        var sampled = 0;

        foreach (var position in Lattice()) {
            if ((sampled++ % 7) != 0) {
                continue;
            }

            foreach (var direction in Directions) {
                foreach (var radius in new[] { 0.0, 0.25 }) {
                    var fixedRadius = FixedQ4816.FromDouble(value: radius);
                    var expectedHit = exact.SphereCast(
                        dir: direction,
                        hit: out var expected,
                        maxDist: maxDistance,
                        origin: position,
                        radius: fixedRadius
                    );
                    var actualHit = banded.SphereCast(
                        dir: direction,
                        hit: out var actual,
                        maxDist: maxDistance,
                        origin: position,
                        radius: fixedRadius
                    );

                    casts++;
                    Assert.Equal(expected: expectedHit, actual: actualHit);

                    if (!expectedHit) {
                        continue;
                    }

                    Assert.Equal(expected: expected.Confidence, actual: actual.Confidence);

                    if (expected.Confidence == WorldQueryConfidence.Exact) {
                        // Both marches converged on the same surface: each hit point's clearance is inside the accept
                        // threshold, whatever route the two took to it.
                        var clearance = (Exact(evaluator: exact, position: actual.Point) - fixedRadius);

                        Assert.True(condition: (clearance <= FixedQ4816.FromDouble(value: 0.001)), userMessage: $"the banded hit at {actual.Point.Local} has clearance {clearance}");
                    }
                }
            }
        }

        Assert.True(condition: (casts > 2_000), userMessage: $"only {casts} casts ran");

        // A descent onto the floor from inside the cast's own band never reads a bound: the field along the ray is
        // at most the height above the plane, which only falls, so every sample is exact and the answer is the exact
        // evaluator's to the bit — TryGroundHeight and the sphere cast alike.
        var slack = ((double)grid.Slack);
        var descents = 0;

        foreach (var x in new[] { -6.2, -2.3, 0.4, 3.1, 7.7 }) {
            foreach (var z in new[] { -6.6, -1.3, 0.2, 2.9, 6.1 }) {
                foreach (var radius in new[] { 0.0, 0.2 }) {
                    var height = (radius + (slack * 0.9));
                    var origin = Position(
                        x: x,
                        y: height,
                        z: z
                    );
                    var fixedRadius = FixedQ4816.FromDouble(value: radius);

                    descents++;
                    Assert.Equal(
                        expected: exact.SphereCast(dir: Down, hit: out var expected, maxDist: maxDistance, origin: origin, radius: fixedRadius),
                        actual: banded.SphereCast(dir: Down, hit: out var actual, maxDist: maxDistance, origin: origin, radius: fixedRadius)
                    );
                    Assert.Equal(expected: expected, actual: actual);

                    if (radius != 0.0) {
                        continue;
                    }

                    Assert.Equal(
                        expected: exact.TryGroundHeight(groundY: out var expectedGround, position: origin, probeDown: maxDistance, probeUp: FixedQ4816.Zero),
                        actual: banded.TryGroundHeight(groundY: out var actualGround, position: origin, probeDown: maxDistance, probeUp: FixedQ4816.Zero)
                    );
                    Assert.Equal(expected: expectedGround.Value, actual: actualGround.Value);
                }
            }
        }

        Assert.Equal(expected: 50, actual: descents);
    }

    [Fact]
    public void TheBakeIsAPureFunctionOfTheProgram() {
        var (exactA, gridA, _) = BuildFixture();
        var (_, gridB, _) = BuildFixture();

        Assert.Equal(expected: gridA.Origin, actual: gridB.Origin);
        Assert.Equal(expected: gridA.Slack.Value, actual: gridB.Slack.Value);
        Assert.Equal(expected: gridA.CornerCount, actual: gridB.CornerCount);

        // Touch the two grids in opposite orders, then compare every corner in the touched region: the values are
        // the exact evaluator's at the corner, whatever order first read them.
        for (var x = 0; (x < 24); x++) {
            for (var y = 0; (y < 12); y++) {
                for (var z = 0; (z < 24); z++) {
                    Assert.True(condition: gridA.TryCornerDistance(distance: out _, material: out _, x: x, y: y, z: z));
                    Assert.True(condition: gridB.TryCornerDistance(distance: out _, material: out _, x: (23 - x), y: (11 - y), z: (23 - z)));
                }
            }
        }

        for (var x = 0; (x < 24); x++) {
            for (var y = 0; (y < 12); y++) {
                for (var z = 0; (z < 24); z++) {
                    Assert.True(condition: gridA.TryCornerDistance(distance: out var a, material: out var materialA, x: x, y: y, z: z));
                    Assert.True(condition: gridB.TryCornerDistance(distance: out var b, material: out var materialB, x: x, y: y, z: z));
                    Assert.Equal(expected: a.Value, actual: b.Value);
                    Assert.Equal(expected: materialA, actual: materialB);
                    Assert.True(condition: exactA.TryDistance(
                        distance: out var field,
                        material: out var fieldMaterial,
                        position: FixedPosition.FromLocal(local: gridA.CornerPosition(x: x, y: y, z: z))
                    ));
                    Assert.Equal(expected: field.Value, actual: a.Value);
                    Assert.Equal(expected: fieldMaterial, actual: materialA);
                }
            }
        }

        Assert.Equal(expected: (24L * 12L * 24L), actual: gridA.BakedCornerCount);
    }

    [Fact]
    public void AProgramWithNothingFiniteToCoverBakesNoGrid() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(
            material: material,
            normal: Vector3.UnitY,
            offset: 0f
        );

        var planeOnly = builder.Build(buildInstanceGrid: false);

        Assert.Null(@object: SdfDistanceGrid.TryCover(
            cellSize: FixedQ4816.FromDouble(value: CellSize),
            exact: new SdfFieldEvaluator(program: planeOnly),
            padding: FixedQ4816.Zero,
            program: planeOnly
        ));

        var empty = new SdfProgramBuilder();

        _ = empty.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        var shapeless = empty.Build(buildInstanceGrid: false);

        Assert.Null(@object: SdfDistanceGrid.TryCoverBox(
            cellSize: FixedQ4816.One,
            exact: new SdfFieldEvaluator(program: shapeless),
            max: Vector(x: 1.0, y: 1.0, z: 1.0),
            min: Vector(x: -1.0, y: -1.0, z: -1.0)
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfDistanceGrid.TryCoverBox(
            cellSize: FixedQ4816.Zero,
            exact: new SdfFieldEvaluator(program: planeOnly),
            max: Vector(x: 1.0, y: 1.0, z: 1.0),
            min: Vector(x: -1.0, y: -1.0, z: -1.0)
        ));
    }
}
