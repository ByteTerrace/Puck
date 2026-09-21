using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class SphereBoxContactLawTests {
    private static FixedQ4816 Scalar(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 Vector(double x, double y, double z) => new(X: Scalar(value: x), Y: Scalar(value: y), Z: Scalar(value: z));
    private static readonly FixedBodyColliderVolume Sphere = new(
        FixedBodyColliderKind.Sphere, FixedVector3.Zero, FixedVector3.Zero, FixedVector3.Zero,
        FixedQuaternion.Identity, Scalar(value: 0.25));
    private static readonly FixedBodyColliderVolume Box = new(
        FixedBodyColliderKind.Box, FixedVector3.Zero, FixedVector3.Zero, Vector(x: 0.5, y: 0.5, z: 0.5),
        FixedQuaternion.Identity, FixedQ4816.Zero);

    [Theory]
    [InlineData(0.70, 0.70, 0.0, false)]
    [InlineData(0.65, 0.65, 0.65, false)]
    [InlineData(0.75, 0.0, 0.0, false)]
    [InlineData(0.70, 0.0, 0.0, true)]
    [InlineData(0.65, 0.65, 0.0, true)]
    [InlineData(0.60, 0.60, 0.60, true)]
    public void FaceEdgeAndCornerUseTheSurfaceRatherThanTheBoundingBox(double x, double y, double z, bool expected) {
        FixedBodyColliderVolume[] spheres = [Sphere];
        FixedBodyColliderVolume[] boxes = [Box];
        var position = Vector(x: x, y: y, z: z);
        var hit = FixedDynamicBodyContacts.TryCorrection(position, FixedQuaternion.Identity, spheres,
            FixedVector3.Zero, FixedQuaternion.Identity, boxes, 0, out var correction);
        Assert.Equal(actual: hit, expected: expected);
        Assert.Equal(expected, FixedStaticCollider.AxisAlignedBox(center: FixedVector3.Zero, halfExtents: Box.HalfExtents)
            .TryGetPush(position, FixedQuaternion.Identity, Sphere, FixedQ4816.Zero, out var push));
        Assert.Equal(correction, (push.Normal * push.Penetration));
        if (expected) {
            var expectedDistance = Math.Sqrt(d: ((Math.Pow(x: Math.Max(val1: (x - 0.5), val2: 0), y: 2) +
                Math.Pow(x: Math.Max(val1: (y - 0.5), val2: 0), y: 2)) + Math.Pow(x: Math.Max(val1: (z - 0.5), val2: 0), y: 2)));
            Assert.InRange(((double)correction.Length), ((0.25 - expectedDistance) - 0.00006), ((0.25 - expectedDistance) + 0.00006));
            Assert.Equal(expected, FixedDynamicBodyContacts.TryCorrection(FixedVector3.Zero, FixedQuaternion.Identity, boxes,
                position, FixedQuaternion.Identity, spheres, 0, out var reverse));
            Assert.Equal(actual: reverse, expected: -correction);
        }
    }

    [Fact]
    public void InteriorSphereAndStaticSphereUseOppositeExitDirections() {
        var position = Vector(x: 0.25, y: 0, z: 0);
        FixedBodyColliderVolume[] spheres = [Sphere];
        FixedBodyColliderVolume[] boxes = [Box];
        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(position, FixedQuaternion.Identity, spheres,
            FixedVector3.Zero, FixedQuaternion.Identity, boxes, 0, out var correction));
        Assert.Equal(Vector(x: 0.5, y: 0, z: 0), correction);
        Assert.True(condition: FixedStaticCollider.Sphere(center: position, radius: Sphere.Radius).TryGetPush(
            FixedVector3.Zero, FixedQuaternion.Identity, Box, FixedQ4816.Zero, out var push));
        Assert.Equal(-correction, (push.Normal * push.Penetration));
    }

    [Fact]
    public void RotatedBoxFaceUsesItsOwnNormal() {
        var rotation = FixedQuaternion.FromAxisAngle(Vector(x: 0, y: 0, z: 1), Scalar(value: (Math.PI / 4)));
        var position = rotation.Rotate(vector: Vector(x: 0.7, y: 0, z: 0));
        FixedBodyColliderVolume[] spheres = [Sphere];
        FixedBodyColliderVolume[] boxes = [Box];
        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(position, FixedQuaternion.Identity, spheres,
            FixedVector3.Zero, rotation, boxes, 0, out var correction));
        Assert.InRange(((double)(correction - rotation.Rotate(vector: Vector(x: 0.05, y: 0, z: 0))).Length), 0, 0.00008);
    }

    [Fact]
    public void OneRawUnitOverlapSurvivesPairSelectionWithoutAllocating() {
        FixedBodyColliderVolume[] spheres = [Sphere];
        FixedBodyColliderVolume[] boxes = [Box];
        var position = new FixedVector3(X: (Scalar(value: 0.75) - FixedQ4816.FromRawBits(value: 1)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        for (var i = 0; (i < 100); i++) {
            FixedDynamicBodyContacts.TryCorrection(position, FixedQuaternion.Identity, spheres,
                FixedVector3.Zero, FixedQuaternion.Identity, boxes, 0, out _);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var allHit = true;
        var correction = FixedVector3.Zero;
        for (var i = 0; (i < 1000); i++) {
            allHit &= FixedDynamicBodyContacts.TryCorrection(position, FixedQuaternion.Identity, spheres,
                FixedVector3.Zero, FixedQuaternion.Identity, boxes, 0, out correction);
        }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(condition: allHit);
        Assert.Equal(FixedQ4816.FromRawBits(value: 1), correction.X);
        Assert.Equal(actual: allocated, expected: 0);
    }
}
