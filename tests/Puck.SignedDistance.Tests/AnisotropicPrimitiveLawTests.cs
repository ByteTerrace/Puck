using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a primitive authored at a non-uniform scale lowers to a native shape whose field is its true distance, not
/// the <see cref="SdfOp.Scale"/> op's lower bound <c>f(S⁻¹p)·min(s)</c>. Each lowered primitive's program carries no
/// non-uniform scale, <see cref="SdfFieldEvaluator"/> accepts it, and at points inside, beside and far from the surface
/// its distance is within <see cref="Tolerance"/> of the scaled primitive's Euclidean distance computed in double
/// precision.
/// </summary>
public sealed class AnisotropicPrimitiveLawTests {
    private const double Tolerance = 2e-4;

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

            if (!(Math.Abs(value: (actual - expected)) <= Tolerance)) {
                failures.Add(item: $"{scale} at {point}: {actual}, expected {expected}");
            }
        }

        Assert.Empty(collection: failures);
    }

    private static SdfFieldEvaluator Evaluator(SdfSolidPrimitive type, Vector3 scale) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(chain: builder.ResetPoint(), material: material, scale: scale, type: type);

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
