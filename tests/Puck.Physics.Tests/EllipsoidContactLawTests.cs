using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class EllipsoidContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    private static FixedBodyColliderVolume Lens => new(FixedBodyColliderKind.Ellipsoid,
        FixedVector3.Zero, FixedVector3.Zero, V(x: 0.036, y: 0.010, z: 0.036), FixedQuaternion.Identity, FixedQ4816.Zero);

    [Fact]
    public void SupportAndBoundsFollowTheCurvedSurfaceAtEveryBodyOrientation() {
        var lens = Lens;
        var tilt = FixedQuaternion.FromAxisAngle(FixedVector3.UnitZ, FixedQ4816.FromDegrees(degrees: 35));
        var center = V(x: 2, y: 3, z: 4);
        var direction = V(x: 0.6, y: 0.8, z: 0);
        var anchor = FixedRigidWitness.LocalSupportPoint(localDirection: direction, volume: lens);
        var x = ((double)(anchor.X / lens.HalfExtents.X));
        var y = ((double)(anchor.Y / lens.HalfExtents.Y));

        Assert.InRange(actual: ((x * x) + (y * y)), high: 1.004, low: 0.996);
        var bounds = FixedColliderBounds.WorldBounds(orientation: tilt, position: center, volume: lens);
        var maximum = (center + tilt.Rotate(vector: FixedRigidWitness.LocalSupportPoint(lens, tilt.RotateInverse(vector: FixedVector3.UnitY))));

        Assert.InRange(Math.Abs(value: ((double)((bounds.Center.Y + bounds.Extent.Y) - maximum.Y))), 0, 0.00004);
        Assert.True(condition: (bounds.Extent.Y < Q(value: 0.036)));
        Assert.Equal(Q(value: 0.036), FixedDynamicBodyContacts.BroadphaseRadius(volumes: [lens]));
    }
    [Fact]
    public void EveryStaticFiniteKindSupportsTheLensAndKeepsItsOldBoxCornersEmpty() {
        var identity = FixedQuaternion.Identity;
        FixedStaticCollider[] surfaces = [
            FixedStaticCollider.AxisAlignedBox(center: V(x: 0, y: -0.5, z: 0), halfExtents: V(x: 1, y: 0.5, z: 1)),
            FixedStaticCollider.Box(V(x: 0, y: -0.5, z: 0), identity, V(x: 1, y: 0.5, z: 1)),
            FixedStaticCollider.RoundedBox(V(x: 0, y: -0.6, z: 0), identity, V(x: 1, y: 0.5, z: 1), Q(value: 0.1)),
            FixedStaticCollider.Cylinder(V(x: 0, y: -0.5, z: 0), Q(value: 1), Q(value: 0.5), identity),
            FixedStaticCollider.Sphere(center: V(x: 0, y: -1, z: 0), radius: Q(value: 1)),
            FixedStaticCollider.Superellipsoid(V(x: 0, y: -0.5, z: 0), identity, V(x: 1, y: 0.5, z: 1), Q(value: 2)),
            FixedStaticCollider.HalfSpace(FixedVector3.Zero, FixedVector3.UnitY),
        ];

        foreach (var surface in surfaces) {
            Assert.True(condition: surface.TryGetPush(V(x: 0, y: 0.009, z: 0), identity, Lens, FixedQ4816.Zero, out var push), userMessage: surface.Kind.ToString());
            Assert.InRange(((double)push.Penetration), 0.0008, 0.0012);
            Assert.True(condition: (push.Normal.Y > Q(value: 0.99)));
            Assert.False(condition: surface.TryGetPush(V(x: 0, y: 0.011, z: 0), identity, Lens, FixedQ4816.Zero, out _), userMessage: surface.Kind.ToString());
        }

        var sphere = FixedStaticCollider.Sphere(center: V(x: 0.034, y: 0.009, z: 0), radius: Q(value: 0.001));

        Assert.False(condition: sphere.TryGetPush(FixedVector3.Zero, identity, Lens, FixedQ4816.Zero, out _));
    }
    [Fact]
    public void LensPairsResolveShallowOverlapAndRejectDiagonalBoxOverlap() {
        var identity = FixedQuaternion.Identity;
        ReadOnlySpan<FixedBodyColliderVolume> lenses = [Lens];

        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.071, y: 0, z: 0), identity, lenses, 0, out var correction));
        Assert.InRange(((double)correction.X), -0.0012, -0.0008);
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.073, y: 0, z: 0), identity, lenses, 0, out _));
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, identity, lenses,
            V(x: 0.060, y: 0.018, z: 0), identity, lenses, 0, out _));
    }
}
