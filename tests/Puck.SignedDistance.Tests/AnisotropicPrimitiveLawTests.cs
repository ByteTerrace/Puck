using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a primitive authored at a non-uniform scale lowers to a native shape whose field is its true distance, not
/// the <see cref="SdfOp.Scale"/> op's lower bound <c>f(S⁻¹p)·min(s)</c>. Each lowered primitive's program carries no
/// non-uniform scale, <see cref="SdfFieldEvaluator"/> accepts it, and at points inside, beside and far from the surface
/// its distance is within <see cref="ToleranceAt"/> of the scaled primitive's Euclidean distance computed in double
/// precision.
/// </summary>
public sealed class AnisotropicPrimitiveLawTests {
    // Q48.16's resolution, which the evaluator's squared distances round to before their square root.
    private const double Resolution = (1.0 / 65536.0);

    // The evaluator's distance at a true distance d: within 2e-4 of it, and within the square root of a few ulps of a
    // squared distance, about Resolution / d away from the surface and up to √Resolution at it.
    private static double ToleranceAt(double distance) =>
        (2e-4 + (Math.Sqrt(d: ((distance * distance) + (3.0 * Resolution))) - Math.Abs(value: distance)));

    // A torus scaled alike in X and Z: its minor section an ellipse in every meridian, revolved at the scaled major radius.
    [InlineData(0.8f, 0.3f)]
    [InlineData(0.5f, 1.4f)]
    [InlineData(1.2f, 0.05f)]
    [Theory]
    public void ATorusScaledAlikeInXAndZAnswersItsTrueDistance(float radial, float vertical) {
        var scale = new Vector3(x: radial, y: vertical, z: radial);
        var evaluator = Evaluator(scale: scale, type: SdfSolidPrimitive.Torus);
        var failures = new List<string>();

        foreach (var point in Points(reach: (1.4f * MathF.Max(radial, vertical)))) {
            // The nearest point lies in the point's own meridian: the section's ellipse about (major, 0) in (radius, y).
            var radius = new Vector2(x: point.X, y: point.Z).Length();
            var expected = EllipseDistanceLawTests.Reference(px: (radius - radial), py: point.Y, semiX: (0.4 * radial), semiY: (0.4 * vertical));
            var actual = Distance(evaluator: evaluator, point: point);

            if (!(Math.Abs(value: (actual - expected)) <= ToleranceAt(distance: expected))) {
                failures.Add(item: $"{scale} at {point}: {actual}, expected {expected}");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }

    // A cylinder scaled differently in X and Z: an elliptic section extruded along its height.
    [InlineData(0.6f, 0.9f, 0.25f)]
    [InlineData(0.15f, 0.4f, 1.1f)]
    [InlineData(1.3f, 0.2f, 0.02f)]
    [Theory]
    public void ACylinderScaledDifferentlyInXAndZAnswersItsTrueDistance(float x, float y, float z) {
        var scale = new Vector3(x: x, y: y, z: z);
        var evaluator = Evaluator(scale: scale, type: SdfSolidPrimitive.Cylinder);
        var failures = new List<string>();

        foreach (var point in Points(reach: (1.4f * MathF.Max(MathF.Max(x, y), z)))) {
            var section = EllipseDistanceLawTests.Reference(px: point.X, py: point.Z, semiX: x, semiY: z);
            var height = (Math.Abs(value: point.Y) - y);
            var expected = (Math.Min(val1: Math.Max(val1: section, val2: height), val2: 0.0) +
                new Vector2(x: (float)Math.Max(val1: section, val2: 0.0), y: (float)Math.Max(val1: height, val2: 0.0)).Length());
            var actual = Distance(evaluator: evaluator, point: point);

            if (!(Math.Abs(value: (actual - expected)) <= ToleranceAt(distance: expected))) {
                failures.Add(item: $"{scale} at {point}: {actual}, expected {expected}");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }

    // An ellipse prism: its section the ellipse its scale's X and Y span, extruded through its Z.
    [InlineData(0.3f, 0.12f, 0.05f)]
    [InlineData(0.1f, 0.45f, 0.6f)]
    [InlineData(0.25f, 0.25f, 0.1f)]
    [Theory]
    public void AnEllipsePrismAnswersItsTrueDistance(float x, float y, float z) {
        var scale = new Vector3(x: x, y: y, z: z);
        var evaluator = Evaluator(profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Ellipse), scale: scale, type: SdfSolidPrimitive.Prism);
        var failures = new List<string>();

        foreach (var point in Points(reach: (1.4f * MathF.Max(MathF.Max(x, y), z)))) {
            // The builder parts a circle's semi-axes by 1e-4, which the reference follows.
            var semiY = ((Math.Abs(value: (x - y)) < 1e-4f) ? (x + 1e-4) : y);
            var expected = Extruded(section: EllipseDistanceLawTests.Reference(px: point.X, py: point.Y, semiX: x, semiY: semiY), depth: (Math.Abs(value: point.Z) - z));
            var actual = Distance(evaluator: evaluator, point: point);

            if (!(Math.Abs(value: (actual - expected)) <= ToleranceAt(distance: expected))) {
                failures.Add(item: $"{scale} at {point}: {actual}, expected {expected}");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }

    // A convex prism: its unit-square vertices scaled by its X and Y into the polygon its section is, extruded through its Z.
    [InlineData(0.3f, 0.12f, 0.05f)]
    [InlineData(1.6f, 0.2f, 0.4f)]
    [InlineData(0.04f, 0.5f, 0.02f)]
    [Theory]
    public void AConvexPrismAnswersItsTrueDistance(float x, float y, float z) {
        Vector2[] unit = [new(x: -0.9f, y: -0.6f), new(x: -0.7f, y: 0.8f), new(x: 0.2f, y: 1f), new(x: 1f, y: 0.1f), new(x: 0.6f, y: -0.9f)];
        var scale = new Vector3(x: x, y: y, z: z);
        var evaluator = Evaluator(profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Convex, CornerRadius: 0f, Vertices: unit), scale: scale, type: SdfSolidPrimitive.Prism);
        Vector2[] polygon = [.. unit.Select(selector: vertex => (vertex * new Vector2(x: x, y: y)))];
        var failures = new List<string>();

        foreach (var point in Points(reach: (1.4f * MathF.Max(MathF.Max(x, y), z)))) {
            var expected = Extruded(section: Polygon(point: new Vector2(x: point.X, y: point.Y), vertices: polygon), depth: (Math.Abs(value: point.Z) - z));
            var actual = Distance(evaluator: evaluator, point: point);

            if (!(Math.Abs(value: (actual - expected)) <= ToleranceAt(distance: expected))) {
                failures.Add(item: $"{scale} at {point}: {actual}, expected {expected}");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }

    // The signed distance to a polygon in double precision: the nearest of its edge segments, negative inside.
    private static double Polygon(Vector2 point, Vector2[] vertices) {
        var nearest = double.MaxValue;
        var inside = false;

        for (var index = 0; (index < vertices.Length); index++) {
            var a = vertices[index];
            var b = vertices[((index + 1) % vertices.Length)];
            double ex = (b.X - a.X), ey = (b.Y - a.Y), px = (point.X - a.X), py = (point.Y - a.Y);
            var t = Math.Clamp(value: (((px * ex) + (py * ey)) / ((ex * ex) + (ey * ey))), min: 0.0, max: 1.0);
            double dx = (px - (t * ex)), dy = (py - (t * ey));

            nearest = Math.Min(val1: nearest, val2: Math.Sqrt(d: ((dx * dx) + (dy * dy))));
            if (((a.Y > point.Y) != (b.Y > point.Y)) && (point.X < (a.X + (((point.Y - a.Y) / (b.Y - a.Y)) * (b.X - a.X))))) {
                inside = !inside;
            }
        }

        return (inside ? -nearest : nearest);
    }
    // A section's distance extruded through a slab: exact inside and out.
    private static double Extruded(double section, double depth) =>
        (Math.Min(val1: Math.Max(val1: section, val2: depth), val2: 0.0) + Math.Sqrt(d: (Math.Pow(x: Math.Max(val1: section, val2: 0.0), y: 2.0) + Math.Pow(x: Math.Max(val1: depth, val2: 0.0), y: 2.0))));
    private static SdfFieldEvaluator Evaluator(SdfSolidPrimitive type, Vector3 scale, SdfPrismProfile? profile = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(chain: builder.ResetPoint(), material: material, profile: profile, scale: scale, type: type);

        var program = builder.Build();

        Assert.DoesNotContain(collection: program.Instructions, filter: static instruction =>
            ((instruction.Op == SdfOp.Scale) && ((instruction.Data0.X != instruction.Data0.Y) || (instruction.Data0.Y != instruction.Data0.Z))));

        return new SdfFieldEvaluator(program: program);
    }
    private static double Distance(SdfFieldEvaluator evaluator, Vector3 point) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ));

        return ((double)distance);
    }
    // A lattice through the primitive's neighbourhood, off every axis and diagonal plane, and a ring far from it.
    private static IEnumerable<Vector3> Points(float reach) {
        float[] steps = [-0.93f, -0.61f, -0.27f, 0.08f, 0.39f, 0.72f, 1.04f];

        foreach (var x in steps) {
            foreach (var y in steps) {
                foreach (var z in steps) {
                    yield return (new Vector3(x: x, y: y, z: z) * reach);
                }
            }
        }
        for (var step = 0; (step < 12); step++) {
            var (sin, cos) = MathF.SinCos(x: (0.3f + ((step * MathF.Tau) / 12f)));

            yield return (new Vector3(x: cos, y: 0.37f, z: sin) * (6f * reach));
        }
    }
}
