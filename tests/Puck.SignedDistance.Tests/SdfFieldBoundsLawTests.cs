using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// <see cref="SdfFieldEvaluator.TryDistanceBounds"/> is certified: over a box, its interval holds every
/// <see cref="SdfFieldEvaluator.TryDistance"/> answer at a point of the box. Each op, shape and blend is swept through
/// boxes from a single point to several units across, its corners and interior points evaluated by the point
/// interpreter; a point box's interval stays within a few raws of its one answer, so a rule widened to the whole line
/// fails as surely as one that misses an answer. The sync laws hold the bounds interpreter's rule sets to the point
/// interpreter's: an op or shape the point side accepts has a rule or a named refusal, never neither.
/// </summary>
public sealed class SdfFieldBoundsLawTests {
    // A point box's interval spans its one answer plus the outward raws of every step between: a rotation's two fused
    // stages, a blend's few products and a norm's root add a handful each.
    private const long PointBoxWidthRaw = 96L;
    private const int BoxesPerCase = 48;
    private const int InteriorPointsPerBox = 12;

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

                if (lower == upper) {
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
        foreach (var op in Enum.GetValues<SdfOp>()) {
            Assert.True(
                condition: (SdfFieldEvaluator.IsSupportedOp(op: op) == SdfFieldEvaluator.BoundedOps.Contains(value: op)),
                userMessage: $"op {op}: the point interpreter {(SdfFieldEvaluator.IsSupportedOp(op: op) ? "accepts" : "refuses")} it but the bounds interpreter {(SdfFieldEvaluator.BoundedOps.Contains(value: op) ? "has" : "has no")} inclusion rule for it"
            );
        }
    }
    [Fact]
    public void EveryInterpretedShapeHasAnInclusionRuleOrANamedRefusal() {
        foreach (var shape in Enum.GetValues<SdfShapeType>()) {
            var bounded = SdfFieldEvaluator.BoundedShapes.ContainsKey(key: shape);
            var refused = SdfFieldEvaluator.UnboundedShapes.ContainsKey(key: shape);

            Assert.False(condition: (bounded && refused), userMessage: $"shape {shape} is both bounded and refused");
            Assert.True(
                condition: (SdfFieldEvaluator.IsSupportedShape(shape: shape) == (bounded || refused)),
                userMessage: $"shape {shape}: the point interpreter {(SdfFieldEvaluator.IsSupportedShape(shape: shape) ? "accepts" : "refuses")} it, and the bounds interpreter {(bounded ? "bounds" : (refused ? "refuses" : "does not name"))} it"
            );
        }

        // Every blend reaches the sweep, so a blend without its own arm (which would fall through to a union) is caught
        // by its case's answers.
        foreach (var blend in Enum.GetValues<SdfBlendOp>()) {
            Assert.True(condition: Programs.ContainsKey(key: $"blend {blend}"), userMessage: $"blend {blend} has no case in the sweep");
        }
    }
    [Fact]
    public void AShapeWithoutAnInclusionRuleIsRefusedByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            bulge: 0f,
            c: (2f * Vector3.UnitY),
            material: material,
            radiusEnd: 0.2f,
            radiusStart: 0.3f,
            strandOffset: 0f,
            strands: 1,
            twist: 0f
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var origin = FixedPosition.FromLocal(local: FixedVector3.Zero);
        var refusal = Assert.Throws<NotSupportedException>(testCode: () => evaluator.TryDistanceBounds(distance: out _, lower: origin, upper: origin));

        Assert.Contains(expectedSubstring: "Sweep", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: evaluator.HasDistanceBounds(refusal: out var named));
        Assert.Contains(actualString: named, comparisonType: StringComparison.Ordinal, expectedSubstring: "Sweep");

        var quartic = new SdfProgramBuilder();

        _ = quartic.Superellipsoid(exponent: 4f, material: quartic.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radii: Vector3.One);

        Assert.False(condition: new SdfFieldEvaluator(program: quartic.Build()).HasDistanceBounds(refusal: out var exponent));
        Assert.Contains(actualString: exponent, comparisonType: StringComparison.Ordinal, expectedSubstring: "Superellipsoid");
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

        foreach (var blend in Enum.GetValues<SdfBlendOp>()) {
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
