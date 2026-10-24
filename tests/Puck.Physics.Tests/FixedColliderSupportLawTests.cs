using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedColliderSupportLawTests {
    private static FixedQ4816 Q(double x) => FixedQ4816.FromDouble(value: x);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(x: x), Y: Q(x: y), Z: Q(x: z));

    [Fact]
    public void EveryFiniteSupportWitnessLiesOnTheQueriedSurface() {
        var center = V(x: 3, y: 2, z: 1);
        var rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, Q(x: 0.3));
        FixedStaticCollider[] colliders = [
            FixedStaticCollider.Sphere(center: center, radius: Q(x: 1)),
            FixedStaticCollider.AxisAlignedBox(center: center, halfExtents: V(x: 1, y: 0.5, z: 0.8)),
            FixedStaticCollider.Box(center, rotation, V(x: 1, y: 0.5, z: 0.8)),
            FixedStaticCollider.RoundedBox(center, rotation, V(x: 1, y: 0.5, z: 0.8), Q(x: 0.1)),
            FixedStaticCollider.Cylinder(center, Q(x: 1), Q(x: 0.5), rotation),
            FixedStaticCollider.ChamferedBox(center, rotation, V(x: 1, y: 0.5, z: 0.8), Q(x: 0.1), Q(x: 0.02)),
            FixedStaticCollider.Superellipsoid(center, rotation, V(x: 0.3, y: 0.045, z: 0.23), Q(x: 4)),
        ];

        foreach (var collider in colliders) {
            foreach (var direction in new[] { FixedVector3.Zero, FixedVector3.UnitX, -FixedVector3.UnitY, V(x: 1, y: 2, z: 3) }) {
                var point = collider.SupportPoint(direction: direction);

                Assert.Equal(point, collider.SupportPoint(direction: direction));
                Assert.True(condition: FixedSurfaceQuery.TryNearest([collider], [], point, Q(x: 10), out var nearest));
                Assert.True(condition: (nearest.Distance <= Q(x: 0.0003)), userMessage: $"{collider.Kind} direction={direction} point={point} nearest={nearest}");
            }
        }
        var halfSpace = FixedStaticCollider.HalfSpace(center, FixedVector3.UnitY);

        Assert.Throws<InvalidOperationException>(testCode: () => halfSpace.SupportPoint(direction: FixedVector3.UnitY));
    }
}
