using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class ConvexContactWorkLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    [Fact]
    public void TheExactQueryRefusesShapesWithTheirOwnAnalyticKernel() {
        var sphere = FixedStaticCollider.Sphere(center: FixedVector3.Zero, radius: FixedQ4816.One);

        Assert.Throws<ArgumentException>(testCode: () => FixedConvexContact.TryPush(sphere, FixedVector3.Zero,
            FixedQuaternion.Identity, default, FixedQ4816.Zero, out _, out _));
    }
    [InlineData(false, FixedBodyColliderKind.Sphere)]
    [InlineData(false, FixedBodyColliderKind.Capsule)]
    [InlineData(false, FixedBodyColliderKind.Box)]
    [InlineData(true, FixedBodyColliderKind.Sphere)]
    [InlineData(true, FixedBodyColliderKind.Capsule)]
    [InlineData(true, FixedBodyColliderKind.Box)]
    [Theory]
    public void TouchingFacesFinishWithoutSearchingForAnEnclosingTetrahedron(bool smooth, FixedBodyColliderKind kind) {
        var collider = (smooth
            ? FixedStaticCollider.Superellipsoid(FixedVector3.Zero, FixedQuaternion.Identity, V(x: 2, y: 1, z: 1), Q(value: 4))
            : FixedStaticCollider.ChamferedBox(FixedVector3.Zero, FixedQuaternion.Identity, V(x: 2, y: 1, z: 1), Q(value: 0.2), FixedQ4816.Zero));
        var body = new FixedBodyColliderVolume(Kind: kind, Center: V(x: 0, y: 0.35, z: 0), Endpoint: V(x: 0, y: 1.71, z: 0),
            HalfExtents: V(x: 0.35, y: 0.35, z: 0.35), Rotation: FixedQuaternion.Identity, Radius: Q(value: 0.35));

        Assert.False(condition: FixedConvexContact.TryPush(collider, V(x: 0, y: 1, z: 0), FixedQuaternion.Identity,
            body, FixedQ4816.Zero, out _, out var touchingWork));
        Assert.InRange(actual: touchingWork, high: ((kind == FixedBodyColliderKind.Box) ? 80 : 16), low: 1);
        Assert.True(condition: FixedConvexContact.TryPush(collider, V(x: 0, y: 0.999, z: 0), FixedQuaternion.Identity,
            body, FixedQ4816.Zero, out var push, out var overlapWork));
        Assert.InRange(((double)push.Penetration), 0.0008, 0.0012);
        Assert.InRange(actual: overlapWork, high: 192, low: 1);
        Assert.True(condition: FixedConvexContact.TryPush(collider, V(x: 0, y: 0.999, z: 0), FixedQuaternion.Identity,
            body, FixedQ4816.Zero, out var repeated, out var repeatedWork));
        Assert.Equal(actual: repeated, expected: push);
        Assert.Equal(actual: repeatedWork, expected: overlapWork);
    }
}
