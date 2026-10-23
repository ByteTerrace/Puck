using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: within a centimetre of a slanted or polygonal side, the deterministic field's gradient is that side's
/// normal. The 2D cores root each candidate distance as a length rather than rooting the smaller squared distance:
/// a squared millimetre distance is a few Q48.16 ulps, and its root steps by whole ulps across the field the mesh
/// bake's gradient taps read, which flattened the gradient to the nearest axis.
/// </summary>
public sealed class ProfileGradientLawTests {
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static Vector3 Gradient(SdfFieldEvaluator field, double x, double y, double z) {
        Assert.True(condition: field.TryFieldGradient(
            epsilon: FixedQ4816.FromDouble(value: 0.0005),
            gradient: out var gradient,
            position: Position(x: x, y: y, z: z)
        ));

        return gradient.ToVector3();
    }
    private static void AssertAligned(Vector3 expected, Vector3 actual) {
        Assert.True(
            condition: (Vector3.Dot(vector1: Vector3.Normalize(value: expected), vector2: Vector3.Normalize(value: actual)) > 0.995f),
            userMessage: $"the gradient {actual} should align with {expected}"
        );
    }

    // A tapered prism (bottom half-width 0.5, top 0.25, half-height 0.45) has a slanted side whose outward normal is
    // (0.963, 0.267) on the +x side; the field within a centimetre of it, inside and out, slopes along that normal.
    [Fact]
    public void ATaperedPrismsFieldSlopesAlongItsSlantedSideNearIt() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            builder.ResetPoint(),
            SdfSolidPrimitive.Prism,
            new Vector3(x: 0.5f, y: 0.45f, z: 0.2f),
            material,
            taper: 0.5f
        );

        var field = new SdfFieldEvaluator(program: builder.Build());
        var normal = Vector3.Normalize(value: new Vector3(x: 0.9f, y: 0.25f, z: 0f));

        // The side at height y runs through x = 0.25 + 0.25 * (0.45 - y) / 0.9; points 3 mm and 8 mm inside and 6 mm
        // outside it along its normal.
        foreach (var y in new[] { 0.426, 0.3, 0.0, -0.3 }) {
            var sideX = (0.25 + (0.25 * ((0.45 - y) / 0.9)));

            foreach (var offset in new[] { -0.008, -0.003, 0.006 }) {
                AssertAligned(expected: normal, actual: Gradient(field: field, x: (sideX + (offset * normal.X)), y: (y + (offset * normal.Y)), z: 0d));
            }
        }
    }
    // A regular hexagon prism's field within a centimetre of a side slopes along that side's normal.
    [Fact]
    public void AConvexPolygonsFieldSlopesAlongItsSideNearIt() {
        var vertices = new Vector2[6];

        for (var index = 0; (index < 6); index++) {
            var angle = (-(MathF.PI / 3f) * index);

            vertices[index] = new Vector2(x: (0.5f * MathF.Cos(x: angle)), y: (0.5f * MathF.Sin(x: angle)));
        }

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().ConvexPolygon(
            vertices,
            0f,
            SdfLift.Extrude,
            0.2f,
            material
        );

        var field = new SdfFieldEvaluator(program: builder.Build());
        // The side between the vertices at 0 and -60 degrees has its outward normal at -30 degrees and lies
        // 0.5 * cos(30 degrees) from the centre.
        var normal = new Vector3(x: MathF.Cos(x: (-MathF.PI / 6f)), y: MathF.Sin(x: (-MathF.PI / 6f)), z: 0f);
        var apothem = (0.5f * MathF.Cos(x: (MathF.PI / 6f)));

        foreach (var along in new[] { -0.15f, 0f, 0.15f }) {
            var tangent = new Vector3(x: -normal.Y, y: normal.X, z: 0f);

            foreach (var offset in new[] { -0.008f, -0.003f, 0.006f }) {
                var point = ((normal * (apothem + offset)) + (tangent * along));

                AssertAligned(expected: normal, actual: Gradient(field: field, x: point.X, y: point.Y, z: 0d));
            }
        }
    }
}
