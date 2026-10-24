using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class SuperellipsoidContactLawTests {
    private static FixedQ4816 Q(double x) => FixedQ4816.FromDouble(value: x);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(x: x), Y: Q(x: y), Z: Q(x: z));

    [InlineData(FixedBodyColliderKind.Sphere)]
    [InlineData(FixedBodyColliderKind.Capsule)]
    [InlineData(FixedBodyColliderKind.Box)]
    [Theory]
    public void AChairSeatSupportsTheBodyAtItsFaceAndLeavesItsOldBoxCornerEmpty(FixedBodyColliderKind kind) {
        var seat = FixedStaticCollider.Superellipsoid(FixedVector3.Zero, FixedQuaternion.Identity, V(x: 0.25, y: 0.045, z: 0.25), Q(x: 4));
        var body = new FixedBodyColliderVolume(Kind: kind, Center: FixedVector3.Zero, Endpoint: V(x: 0, y: 0.1, z: 0),
            HalfExtents: V(x: 0.001, y: 0.001, z: 0.001), Radius: Q(x: 0.001), Rotation: FixedQuaternion.Identity);

        Assert.False(condition: seat.TryGetPush(V(x: 0.25, y: 0.045, z: 0.25), FixedQuaternion.Identity, body, FixedQ4816.Zero, out _));
        Assert.True(condition: seat.TryGetPush(V(x: 0, y: 0.045, z: 0), FixedQuaternion.Identity, body, FixedQ4816.Zero, out var push));
        Assert.InRange(((double)push.Penetration), 0.0008, 0.0012);
        Assert.Equal(FixedVector3.UnitY, push.Normal);
        Assert.False(condition: push.IsEdge);
    }
    [Fact]
    public void TheInteriorAndRotatedSurfaceQueriesUseTheSameSolid() {
        var shape = FixedStaticCollider.Superellipsoid(FixedVector3.Zero, FixedQuaternion.Identity, V(x: 0.25, y: 0.045, z: 0.25), Q(x: 4));

        Assert.True(condition: FixedSurfaceQuery.TryNearest([shape], [], FixedVector3.Zero, Q(x: 1), out var inside));
        Assert.InRange(((double)inside.Distance), 0.0448, 0.0452);
        Assert.InRange(((double)FixedQ4816.Abs(value: inside.Point.Y)), 0.0448, 0.0452);
        var rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitZ, FixedQ4816.FromDegrees(degrees: 8));

        shape = shape with { Rotation = rotation, Center = V(x: 3, y: 2, z: 1) };
        var probe = (shape.Center + rotation.Rotate(vector: V(x: 0.3, y: 0.1, z: 0.3)));

        Assert.True(condition: FixedSurfaceQuery.TryNearest([shape], [], probe, Q(x: 1), out var surface));
        var p = rotation.RotateInverse(vector: (surface.Point - shape.Center));
        var gauge = Math.Pow(x: ((Math.Pow(x: ((double)(p.X / Q(x: 0.25))), y: 4) + Math.Pow(x: ((double)(p.Y / Q(x: 0.045))), y: 4)) +
            Math.Pow(x: ((double)(p.Z / Q(x: 0.25))), y: 4)), y: 0.25);

        Assert.InRange(actual: gauge, high: 1.001, low: 0.999);
    }
}
