using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a primitive's reach describes the geometry its own emission produces. Emission reads a scale's
/// magnitude, so reach must too — a signed maximum reports a negative reach for a scale with no positive component,
/// every consumer folds reach into a running maximum seeded at zero, and the instance ships a cull bound of nothing
/// but its margin around geometry that is still there.
/// <para>Each arm pairs a mirrored-sign denial with the positive control it must equal.</para>
/// </summary>
public sealed class SdfSolidGeometryReachLawTests {
    private static readonly Vector3 Anisotropic = new(
        x: 2f,
        y: 3f,
        z: 4f
    );

    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));

    [Fact]
    public void ReachReadsScaleMagnitudes() {
        foreach (var type in Enum.GetValues<SdfSolidPrimitive>()) {
            foreach (var signs in new[] {
                new Vector3(x: -1f, y: 1f, z: 1f),
                new Vector3(x: 1f, y: -1f, z: 1f),
                new Vector3(x: 1f, y: 1f, z: -1f),
                new Vector3(value: -1f),
            }) {
                Assert.Equal(
                    actual: SdfSolidGeometry.Reach(
                        scale: (Anisotropic * signs),
                        type: type
                    ),
                    expected: SdfSolidGeometry.Reach(
                        scale: Anisotropic,
                        type: type
                    )
                );
            }
        }
    }
    [Fact]
    public void ReachCoversTheEmittedSurfaceUnderAMirroredScale() {
        // The whole point of a reach: the emitted surface sits inside a sphere of that radius. A sign-flipped scale
        // emits the same geometry, so a reach that shrinks under the flip is a bound that no longer covers it.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: builder.ResetPoint(),
            material: material,
            scale: -Anisotropic,
            type: SdfSolidPrimitive.Sphere
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var reach = SdfSolidGeometry.Reach(
            scale: -Anisotropic,
            type: SdfSolidPrimitive.Sphere
        );

        Assert.True(condition: evaluator.TryDistance(
            distance: out var inside,
            material: out _,
            position: Position(
                x: 0d,
                y: 0d,
                z: 3.9d
            )
        ));
        Assert.True(
            condition: (inside < FixedQ4816.Zero),
            userMessage: $"the emitted surface does not reach z = 3.9 (field {inside})"
        );
        Assert.True(
            condition: (reach >= 4f),
            userMessage: $"reach {reach} does not cover an emitted surface at |z| = 4"
        );
    }
}
