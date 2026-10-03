using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// <see cref="SdfFieldEvaluator.TryDistanceBounds(Puck.Maths.FixedPosition, Puck.Maths.FixedPosition, out Puck.Maths.FixedInterval)"/> is certified: over a box, its interval holds every
/// <see cref="SdfFieldEvaluator.TryDistance"/> answer at a point of the box. Each op, shape and blend is swept through
/// boxes from a single point to several units across, its corners and interior points evaluated by the point
/// interpreter; a point box's interval stays within a few raws of its one answer, so a rule widened to the whole line
/// fails as surely as one that misses an answer; only a sweep, whose rule encloses every parameter its point search
/// could pick rather than following the search, is exempt from that width. The sync laws hold the bounds interpreter's
/// rule sets to the point interpreter's: every op and shape the point side accepts has a rule.
/// </summary>
public sealed class SdfFieldBoundsLawTests {
    // A point box's interval spans its one answer plus the outward raws of every step between: a rotation's two fused
    // stages, a blend's few products and a norm's root add a handful each.
    private const long PointBoxWidthRaw = 96L;
    private const int BoxesPerCase = 48;
    private const int InteriorPointsPerBox = 12;

    // The cases whose rule encloses a search instead of mirroring a step, so a point box is not a few raws wide.
    private static readonly HashSet<string> EnclosingCases = ["shape Sweep", "shape Sweep bulged and twisted"];

    public static TheoryData<string> Cases() {
        var data = new TheoryData<string>();

        foreach (var name in Programs.Keys) {
            data.Add(row: name);
        }

        return data;
    }
    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void EveryPointAnswerInsideABoxLiesInsideItsBounds(string name) {
        var evaluator = new SdfFieldEvaluator(program: Programs[name]());
        var random = new Random(Seed: StableSeed(name: name));

        for (var box = 0; (box < BoxesPerCase); box++) {
            var center = new Vector3(
                x: NextSigned(random: random, scale: 3f),
                y: NextSigned(random: random, scale: 3f),
                z: NextSigned(random: random, scale: 3f)
            );
            // Half sizes from zero (a point box) through a few units, log-spread so both tiny and wide boxes appear.
            var half = (((box % 8) == 0)
                ? Vector3.Zero
                : new Vector3(
                    x: MathF.Pow(x: 2f, y: (NextSigned(random: random, scale: 6f) - 4f)),
                    y: MathF.Pow(x: 2f, y: (NextSigned(random: random, scale: 6f) - 4f)),
                    z: MathF.Pow(x: 2f, y: (NextSigned(random: random, scale: 6f) - 4f))
                ));
            var lower = Fixed(value: (center - half));
            var upper = Fixed(value: (center + half));

            Assert.True(condition: evaluator.TryDistanceBounds(
                distance: out var bounds,
                lower: FixedPosition.FromLocal(local: lower),
                upper: FixedPosition.FromLocal(local: upper)
            ));

            foreach (var point in PointsInside(interior: InteriorPointsPerBox, lower: lower, random: random, upper: upper)) {
                Assert.True(condition: evaluator.TryDistance(
                    distance: out var distance,
                    material: out _,
                    position: FixedPosition.FromLocal(local: point)
                ));
                Assert.True(
                    condition: bounds.Contains(value: distance),
                    userMessage: $"{name}: the point answer {distance} at {point} lies outside {bounds}, the bounds of the box [{lower}, {upper}]"
                );

                if ((lower == upper) && !EnclosingCases.Contains(item: name)) {
                    Assert.True(
                        condition: ((((Int128)bounds.Upper.Value) - bounds.Lower.Value) <= PointBoxWidthRaw),
                        userMessage: $"{name}: the point box at {point} answers {distance} but bounds it by {bounds}, wider than {PointBoxWidthRaw} raws"
                    );
                }
            }
        }
    }
    [Fact]
    public void EveryInterpretedOpHasAnInclusionRule() {
        var exercised = Programs.Values.SelectMany(selector: build => build().Instructions).Select(selector: instruction => instruction.Op).ToHashSet();

        foreach (var op in Enum.GetValues<SdfOp>()) {
            Assert.True(
                condition: (SdfFieldEvaluator.IsSupportedOp(op: op) == SdfFieldEvaluator.BoundedOps.Contains(value: op)),
                userMessage: $"op {op}: the point interpreter {(SdfFieldEvaluator.IsSupportedOp(op: op) ? "accepts" : "refuses")} it but the bounds interpreter {(SdfFieldEvaluator.BoundedOps.Contains(value: op) ? "has" : "has no")} inclusion rule for it"
            );

            if (SdfFieldEvaluator.IsSupportedOp(op: op)) {
                Assert.Contains(collection: exercised, expected: op);
            }
        }
    }
    [Fact]
    public void EveryInterpretedShapeHasAnInclusionRule() {
        var exercised = Programs.Values.SelectMany(selector: build => build().Instructions)
            .Where(predicate: instruction => ((instruction.Op == SdfOp.ShapeBlend) && !instruction.Detail))
            .Select(selector: instruction => ((SdfShapeType)instruction.Shape)).ToHashSet();

        foreach (var shape in Enum.GetValues<SdfShapeType>()) {
            var bounded = SdfFieldEvaluator.BoundedShapes.Contains(value: shape);

            Assert.True(
                condition: (SdfFieldEvaluator.IsSupportedShape(shape: shape) == bounded),
                userMessage: $"shape {shape}: the point interpreter {(SdfFieldEvaluator.IsSupportedShape(shape: shape) ? "accepts" : "refuses")} it, and the bounds interpreter {(bounded ? "has" : "has no")} inclusion rule for it"
            );

            if (bounded) {
                Assert.Contains(collection: exercised, expected: shape);
            }
        }

        foreach (var blend in Enum.GetValues<SdfBlendOp>()) {
            Assert.True(condition: Programs.ContainsKey(key: $"blend {blend}"), userMessage: $"blend {blend} has no case in the sweep");
        }
    }
    [Fact]
    public void MorphBoundsEncloseTheExactWeightAtSurfaceContact() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 1f)
            .PushFieldMorph(from: -1f, laneIndex: 0, to: 2f)
            .Plane(material: material, normal: Vector3.UnitY, offset: -2f).PopField();

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.Zero, upper: FixedPosition.Zero));
        // The exact weight is 1/3, so (1 - 1/3) * 1 + (1/3) * -2 is zero.
        Assert.True(condition: bounds.Contains(value: FixedQ4816.Zero));
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(boundsQueryBudget: 64, from: FixedPosition.Zero, sight: out var sight, to: FixedPosition.Zero));
        Assert.NotEqual(expected: SdfCertifiedVisibility.Clear, actual: sight.Visibility);
    }
    [Fact]
    public void StairsBoundsEncloseTheExactThird() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .PushFieldStairs(radius: 1f, steps: 3)
            .Plane(material: material, normal: Vector3.UnitY, offset: 0f).PopField();

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.Zero, upper: FixedPosition.Zero));
        // With both input fields zero, radius one and three steps, the exact field is -1/3.
        Assert.True(condition: ((((Int128)bounds.Lower.Value) * 3) <= -FixedQ4816.One.Value));
        Assert.True(condition: ((((Int128)bounds.Upper.Value) * 3) >= -FixedQ4816.One.Value));
    }
    [Fact]
    public void CellBoundsEncloseTheUnroundedFeaturePosition() {
        var builder = new SdfProgramBuilder();

        _ = builder.Plane(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), normal: Vector3.UnitY, offset: 0f)
            .CellDisplace(amplitude: 1f, frequency: 1f, mode: SdfCellMode.F1, randomness: 0.25f, seed: 7u);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var position = FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: 28222L),
            Y: FixedQ4816.FromRawBits(value: 35997L),
            Z: FixedQ4816.FromRawBits(value: 35425L)
        ));
        var hash = Pcg3dLatticeNoise.Pcg3d(x: 7u, y: 7u ^ 0x9E3779B9u, z: 7u ^ 0x85EBCA77u);

        Assert.Equal(actual: ((hash.X >> 16), (hash.Y >> 16), (hash.Z >> 16)), expected: (14585u, 45685u, 43396u));
        Assert.True(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: position, upper: position));
        // The nearest feature is (28222.25, 35997.25, 35425) raws: the field is 3229 + sqrt(2)/4 raws.
        Assert.True(condition: (bounds.Lower.Value <= 3229L));
        Assert.True(condition: (bounds.Upper.Value >= 3230L));
    }
    [Fact]
    public void ScaleBoundsEncloseTheUnroundedProduct() {
        var builder = new SdfProgramBuilder();
        var scale = (1f + (1f / 65536f));

        _ = builder.Scale(scale: new Vector3(value: scale)).Scale(scale: new Vector3(value: scale))
            .Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.Zero, upper: FixedPosition.Zero));
        var numerator = -(((Int128)65537) * 65537);

        Assert.True(condition: ((((Int128)bounds.Lower.Value) * 65536) <= numerator));
        Assert.True(condition: ((((Int128)bounds.Upper.Value) * 65536) >= numerator));
    }

    // One program per op, shape and blend, each placed off the origin, rotated and composed so every rule meets a
    // transformed point.
    private static readonly IReadOnlyDictionary<string, Func<SdfProgram>> Programs = BuildPrograms();

    private static Dictionary<string, Func<SdfProgram>> BuildPrograms() {
        var programs = new Dictionary<string, Func<SdfProgram>>();
        var tilt = Quaternion.Normalize(value: new Quaternion(w: 0.9f, x: 0.2f, y: -0.35f, z: 0.1f));
        Vector2[] hexagon = [new(x: 1f, y: 0f), new(x: 0.5f, y: -0.8f), new(x: -0.5f, y: -0.8f), new(x: -1f, y: 0f), new(x: -0.5f, y: 0.8f), new(x: 0.5f, y: 0.8f)];

        void Shape(string name, Action<SdfProgramBuilder, int> emit) =>
            programs[name] = () => {
                var builder = new SdfProgramBuilder();
                var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

                _ = builder.Translate(offset: new Vector3(x: 0.3f, y: -0.2f, z: 0.4f)).Rotate(rotation: tilt);
                emit(arg1: builder, arg2: material);

                return builder.Build();
            };

        Shape(name: "shape Sphere", emit: (b, m) => b.Sphere(material: m, radius: 1.1f));
        Shape(name: "shape Box", emit: (b, m) => b.Box(halfExtents: new Vector3(x: 1f, y: 0.6f, z: 0.4f), material: m, round: 0.1f));
        Shape(name: "shape ScreenSlab", emit: (b, _) => b.ScreenSlab(halfExtents: new Vector3(x: 1f, y: 0.6f, z: 0.05f), round: 0.02f));
        Shape(name: "shape Capsule", emit: (b, m) => b.Capsule(endpoint: new Vector3(x: 0.5f, y: 1.5f, z: -0.2f), material: m, radius: 0.4f));
        Shape(name: "shape Torus", emit: (b, m) => b.Torus(majorRadius: 1.2f, material: m, minorRadius: 0.3f));
        Shape(name: "shape Cylinder", emit: (b, m) => b.Cylinder(halfHeight: 0.8f, material: m, radius: 0.6f, rounding: 0.05f));
        Shape(name: "shape Plane", emit: (b, m) => b.Plane(material: m, normal: Vector3.Normalize(value: new Vector3(x: 0.2f, y: 1f, z: -0.3f)), offset: 0.25f));
        Shape(name: "shape Vesica", emit: (b, m) => b.Vesica(halfSeparation: 0.5f, material: m, radius: 1f));
        Shape(name: "shape RoundCone", emit: (b, m) => b.RoundCone(height: 1.4f, lowerRadius: 0.6f, material: m, upperRadius: 0.25f));
        Shape(name: "shape Superellipsoid", emit: (b, m) => b.Superellipsoid(exponent: 2f, material: m, radii: new Vector3(x: 1.2f, y: 0.7f, z: 0.9f)));

        // The shipped worlds' exponents (2.05 banks and rocks, 2.1 avatar parts, 2.4 and 3 figures) and the range's ends.
        foreach (var exponent in ((float[])[2.05f, 2.1f, 2.4f, 3f, 4f, 8f])) {
            Shape(name: $"shape Superellipsoid {exponent}", emit: (b, m) => b.Superellipsoid(exponent: exponent, material: m, radii: new Vector3(x: 1.2f, y: 0.7f, z: 0.9f)));
        }

        Shape(name: "shape Sweep", emit: (b, m) => b.Sweep(a: Vector3.Zero, b: Vector3.UnitY, bulge: 0f, c: (2f * Vector3.UnitY), material: m, radiusEnd: 0.2f, radiusStart: 0.3f, strandOffset: 0f, strands: 1, twist: 0f));
        Shape(name: "shape Sweep bulged and twisted", emit: (b, m) => b.Sweep(a: new Vector3(x: -1f, y: 0f, z: 0.2f), b: new Vector3(x: 0.4f, y: 1.6f, z: -0.5f), bulge: 0.15f, c: new Vector3(x: 1.2f, y: 0.3f, z: 0.6f), material: m, radiusEnd: 0.12f, radiusStart: 0.25f, strandOffset: 0.2f, strands: 1, twist: 1.5f));

        foreach (var lift in Enum.GetValues<SdfLift>()) {
            Shape(name: $"shape RoundedRectangle {lift}", emit: (b, m) => b.RoundedRectangle(cornerRadius: 0.2f, halfHeight: 0.6f, halfWidth: 0.9f, lift: lift, liftAmount: 0.3f, material: m, capChamfer: 0.05f));
            Shape(name: $"shape Trapezoid {lift}", emit: (b, m) => b.Trapezoid(bottomHalfWidth: 0.9f, halfHeight: 0.6f, lift: lift, liftAmount: 0.3f, material: m, topHalfWidth: 0.4f));
            Shape(name: $"shape ChamferedRectangle {lift}", emit: (b, m) => b.ChamferedRectangle(chamfer: 0.15f, halfHeight: 0.6f, halfWidth: 0.9f, lift: lift, liftAmount: 0.3f, material: m));
            Shape(name: $"shape ConvexPolygon {lift}", emit: (b, m) => b.ConvexPolygon(cornerRadius: 0f, lift: lift, liftAmount: 0.3f, material: m, vertices: hexagon));
        }

        Shape(name: "op Scale", emit: (b, m) => b.Scale(scale: new Vector3(x: 1.5f, y: 1.5f, z: 1.5f)).Box(halfExtents: new Vector3(x: 0.5f, y: 0.4f, z: 0.3f), material: m, round: 0f));
        Shape(name: "op Repeat", emit: (b, m) => b.Repeat(spacing: new Vector3(x: 2f, y: 0f, z: 1.5f)).Sphere(material: m, radius: 0.4f));
        Shape(name: "op RepeatLimited", emit: (b, m) => b.RepeatLimited(limit: new Vector3(x: 2f, y: 1f, z: 0f), spacing: new Vector3(x: 1.25f, y: 1f, z: 1f)).Sphere(material: m, radius: 0.3f));
        Shape(name: "op SymmetryPlane", emit: (b, m) => b.SymmetryPlane(normal: Vector3.Normalize(value: new Vector3(x: 1f, y: 0.5f, z: 0f)), offset: 0.2f).Translate(offset: Vector3.UnitX).Sphere(material: m, radius: 0.5f));
        Shape(name: "op Elongate", emit: (b, m) => b.Elongate(extents: new Vector3(x: 0.5f, y: 0f, z: 0.25f)).Sphere(material: m, radius: 0.5f));
        Shape(name: "op Onion", emit: (b, m) => b.Sphere(material: m, radius: 1f).Onion(thickness: 0.1f));
        Shape(name: "op Dilate", emit: (b, m) => b.Box(halfExtents: new Vector3(x: 0.6f, y: 0.6f, z: 0.6f), material: m, round: 0f).Dilate(radius: 0.15f));

        foreach (var mode in Enum.GetValues<SdfCellMode>()) {
            Shape(name: $"op CellDisplace {mode}", emit: (b, m) => b.Sphere(material: m, radius: 1f).CellDisplace(amplitude: 0.1f, frequency: 1.5f, mode: mode, randomness: (0.5f * SdfCellDisplacement.MaxRandomness(mode: mode)), seed: 7u));
        }

        Shape(name: "op PushField PopField", emit: (b, m) => b.Sphere(material: m, radius: 0.7f).PushField(compose: SdfBlendOp.SmoothUnion, smooth: 0.3f).ResetPoint().Translate(offset: Vector3.UnitX).Box(halfExtents: new Vector3(x: 0.4f, y: 0.4f, z: 0.4f), material: m, round: 0f).PopField());

        foreach (var blend in ((SdfBlendOp[])[
            SdfBlendOp.Union, SdfBlendOp.SmoothUnion, SdfBlendOp.Subtraction, SdfBlendOp.Intersection,
            SdfBlendOp.Xor, SdfBlendOp.SmoothIntersection, SdfBlendOp.SmoothSubtraction,
            SdfBlendOp.ChamferUnion, SdfBlendOp.ChamferIntersection, SdfBlendOp.ChamferSubtraction,
            SdfBlendOp.GrooveUnion, SdfBlendOp.PipeUnion, SdfBlendOp.Morph,
            SdfBlendOp.GrooveSubtraction, SdfBlendOp.PipeSubtraction, SdfBlendOp.StairsUnion, SdfBlendOp.StairsSubtraction,
        ])) {
            Shape(name: $"blend {blend}", emit: (b, m) => {
                _ = b.Sphere(material: m, radius: 0.9f);

                switch (blend) {
                    case SdfBlendOp.Morph:
                        _ = b.PushFieldMorph(from: -1f, laneIndex: 0, to: 1f).ResetPoint().Translate(offset: new Vector3(x: 0.7f, y: 0f, z: 0f)).Torus(majorRadius: 0.8f, material: m, minorRadius: 0.2f).PopField();
                        break;
                    case SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction:
                        _ = b.PushFieldStairs(radius: 0.4f, steps: 3, subtraction: (blend == SdfBlendOp.StairsSubtraction)).ResetPoint().Translate(offset: new Vector3(x: 0.7f, y: 0f, z: 0f)).Torus(majorRadius: 0.8f, material: m, minorRadius: 0.2f).PopField();
                        break;
                    default:
                        _ = b.ResetPoint().Translate(offset: new Vector3(x: 0.7f, y: 0f, z: 0f)).Torus(blend: blend, majorRadius: 0.8f, material: m, minorRadius: 0.2f, smooth: 0.3f);
                        break;
                }
            });
        }

        return programs;
    }
    private static IEnumerable<FixedVector3> PointsInside(FixedVector3 lower, FixedVector3 upper, Random random, int interior) {
        for (var corner = 0; (corner < 8); corner++) {
            yield return new FixedVector3(
                X: (((corner & 1) == 0) ? lower.X : upper.X),
                Y: (((corner & 2) == 0) ? lower.Y : upper.Y),
                Z: (((corner & 4) == 0) ? lower.Z : upper.Z)
            );
        }

        for (var index = 0; (index < interior); index++) {
            yield return new FixedVector3(
                X: Between(lower: lower.X, random: random, upper: upper.X),
                Y: Between(lower: lower.Y, random: random, upper: upper.Y),
                Z: Between(lower: lower.Z, random: random, upper: upper.Z)
            );
        }
    }
    private static FixedQ4816 Between(FixedQ4816 lower, FixedQ4816 upper, Random random) =>
        FixedQ4816.FromRawBits(value: (lower.Value + random.NextInt64(minValue: 0L, maxValue: ((upper.Value - lower.Value) + 1L))));
    private static FixedVector3 Fixed(Vector3 value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z)
        );
    private static float NextSigned(Random random, float scale) =>
        (((((float)random.NextDouble()) * 2f) - 1f) * scale);
    // A seed fixed by the case's name, so a case's boxes never depend on which other cases ran.
    private static int StableSeed(string name) {
        var hash = 17;

        foreach (var character in name) {
            hash = unchecked(((hash * 31) + character));
        }

        return hash;
    }
}
