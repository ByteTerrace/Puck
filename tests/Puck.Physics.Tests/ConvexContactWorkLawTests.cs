using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class ConvexContactWorkLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    [Fact]
    public void SurfaceOffsetsDetectShallowOverlapAndRejectSeparation() {
        var identity = FixedQuaternion.Identity;
        FixedStaticCollider[] shapes = [
            FixedStaticCollider.ChamferedBox(FixedVector3.Zero, identity, V(x: 2, y: 1, z: 1), Q(value: 0.2), Q(value: 0.02)),
            FixedStaticCollider.ChamferedBox(FixedVector3.Zero, identity, V(x: 2, y: 1, z: 1), Q(value: 0.4), Q(value: 0)),
            FixedStaticCollider.Superellipsoid(FixedVector3.Zero, identity, V(x: 0.25, y: 0.045, z: 0.25), Q(value: 4)),
            FixedStaticCollider.RoundedBox(FixedVector3.Zero, identity, V(x: 2, y: 1, z: 1), Q(value: 0.2)),
            FixedStaticCollider.Cylinder(FixedVector3.Zero, Q(value: 1), Q(value: 0.5), identity),
        ];
        FixedQ4816[] components = [-FixedQ4816.One, Q(value: -0.7), Q(value: -0.3), Q(value: -0.1),
            FixedQ4816.Zero, Q(value: 0.1), Q(value: 0.3), Q(value: 0.7), FixedQ4816.One];

        foreach (var shape in shapes) {
            foreach (var x in components) {
                foreach (var y in components) {
                    foreach (var z in components) {
                        if ((x == FixedQ4816.Zero) && (y == FixedQ4816.Zero) && (z == FixedQ4816.Zero)) { continue; }
                        var normal = new FixedVector3(X: x, Y: y, Z: z).Normalize();
                        var surface = shape.SupportPoint(direction: normal);

                        foreach (var kind in new[] { FixedBodyColliderKind.Sphere, FixedBodyColliderKind.Capsule }) {
                            // The capsule extends away from the surface, leaving the same spherical end in contact.
                            var body = new FixedBodyColliderVolume(kind, FixedVector3.Zero, (normal * Q(value: 0.6)),
                                FixedVector3.Zero, identity, Q(value: 0.35));
                            var position = (surface + (normal * Q(value: 0.349)));

                            Assert.True(condition: shape.TryGetPush(position, identity, body, FixedQ4816.Zero, out var push),
                                userMessage: $"Miss: {shape.Kind}, fillet={shape.Radius}, body={kind}, normal={normal}");
                            Assert.True(condition: (push.Penetration > FixedQ4816.Zero));
                            Assert.False(condition: shape.TryGetPush((surface + (normal * Q(value: 0.351))), identity, body, FixedQ4816.Zero, out _),
                                userMessage: $"False contact: {shape.Kind}, fillet={shape.Radius}, body={kind}, normal={normal}");
                        }
                    }
                }
            }
        }
    }
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
