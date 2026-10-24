using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class ChamferedBoxContactLawTests {
    private static FixedQ4816 Q(double x) => FixedQ4816.FromDouble(value: x);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(x: x), Y: Q(x: y), Z: Q(x: z));

    private static readonly FixedStaticCollider Cube = FixedStaticCollider.ChamferedBox(FixedVector3.Zero,
        FixedQuaternion.Identity, V(x: 2, y: 2, z: 2), Q(x: 0.4), FixedQ4816.Zero);

    private static FixedBodyColliderVolume Body(FixedBodyColliderKind kind) => new(Kind: kind,
        Center: FixedVector3.Zero, Endpoint: V(x: 0, y: 0.4, z: 0), HalfExtents: V(x: 0.01, y: 0.01, z: 0.01),
        Rotation: FixedQuaternion.Identity, Radius: Q(x: 0.01));

    [InlineData(FixedBodyColliderKind.Sphere)]
    [InlineData(FixedBodyColliderKind.Capsule)]
    [InlineData(FixedBodyColliderKind.Box)]
    [Theory]
    public void EveryVolumeMeetsTheFaceAndNotTheRemovedCorner(FixedBodyColliderKind kind) {
        var body = Body(kind: kind);

        Assert.False(condition: Cube.TryGetPush(V(x: 2, y: 2, z: 2), FixedQuaternion.Identity, body, FixedQ4816.Zero, out _));
        Assert.True(condition: Cube.TryGetPush(V(x: 0, y: 2, z: 0), FixedQuaternion.Identity, body, FixedQ4816.Zero, out var push));
        Assert.InRange(((double)push.Normal.Y), 0.999, 1.001);
        Assert.InRange(((double)push.Penetration), 0.0098, 0.0102);
        Assert.False(condition: Cube.TryGetPush(V(x: 0, y: 2.011, z: 0), FixedQuaternion.Identity, body, FixedQ4816.Zero, out _));
    }
    [Fact]
    public void SurfaceHoldAndTetherFindTheExactBevelAndRotatedCopy() {
        var probe = V(x: 2, y: 2, z: 0);

        Assert.True(condition: FixedSurfaceQuery.TryNearest([Cube], [], probe, Q(x: 1), out var surface));
        Assert.InRange(((double)surface.Point.X), 1.7998, 1.8002);
        Assert.InRange(((double)surface.Point.Y), 1.7998, 1.8002);
        Assert.InRange(((double)surface.Point.Z), -0.0002, 0.0002);
        var rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, Q(x: 0.4));
        var turned = Cube with { Center = V(x: 10, y: 2, z: -4), Rotation = rotation };

        probe = (turned.Center + rotation.Rotate(vector: V(x: 2, y: 2, z: 0)));
        var direction = rotation.Rotate(vector: V(x: -1, y: -1, z: 0));

        Assert.True(condition: FixedSurfaceQuery.TryNearestDirected([turned], [], probe, direction, Q(x: 1), Q(x: 0.01), out var tether));
        var local = rotation.RotateInverse(vector: (tether.Point - turned.Center));

        Assert.InRange(((double)local.X), 1.7997, 1.8003);
        Assert.InRange(((double)local.Y), 1.7997, 1.8003);
    }
    [InlineData(FixedBodyColliderKind.Sphere)]
    [InlineData(FixedBodyColliderKind.Capsule)]
    [InlineData(FixedBodyColliderKind.Box)]
    [Theory]
    public void ABevelContactSeparatesTheBodyAndRepeatsBitForBit(FixedBodyColliderKind kind) {
        var body = Body(kind: kind);
        var position = V(x: 1.805, y: 1.805, z: 0);

        Assert.True(condition: Cube.TryGetPush(position, FixedQuaternion.Identity, body, FixedQ4816.Zero, out var first));
        Assert.True(condition: Cube.TryGetPush(position, FixedQuaternion.Identity, body, FixedQ4816.Zero, out var repeated));
        Assert.Equal(actual: repeated, expected: first);
        Assert.True(condition: ((first.Normal.X >= Q(x: 0.70)) && (first.Normal.X <= Q(x: 0.72))), userMessage: first.ToString());
        Assert.True(condition: ((first.Normal.Y >= Q(x: 0.70)) && (first.Normal.Y <= Q(x: 0.72))), userMessage: first.ToString());
        Assert.True(condition: first.IsEdge);
        position += (first.Normal * (first.Penetration + Q(x: 0.001)));
        Assert.False(condition: Cube.TryGetPush(position, FixedQuaternion.Identity, body, FixedQ4816.Zero, out _));
    }
    [Fact]
    public void FallingUsesTheBevelButLateralApproachStillNeedsTheFullStep() {
        var body = Body(kind: FixedBodyColliderKind.Sphere);
        var solver = new FixedStaticContactSolver(ContactSkin: FixedQ4816.Zero, GroundedThreshold: Q(x: 0.5), MaxIterations: 8);
        var target = V(x: 1.805, y: 1.805, z: 0);
        var position = target;
        var velocity = -FixedVector3.UnitY;
        var falling = solver.ResolveSweep([Cube], [], V(x: 1.805, y: 1.9, z: 0), ref position, ref velocity,
            FixedQuaternion.Identity, [body], FixedVector3.UnitY);

        Cube.TryGetPush(target, FixedQuaternion.Identity, body, FixedQ4816.Zero, out var diagnosticPush);
        Assert.True(condition: falling.Grounded, userMessage: $"{falling}; {diagnosticPush}");
        position = target;
        velocity = -FixedVector3.UnitX;
        var walking = solver.ResolveSweep([Cube], [], V(x: 1.9, y: 1.805, z: 0), ref position, ref velocity,
            FixedQuaternion.Identity, [body], FixedVector3.UnitY);

        Assert.False(condition: walking.Grounded);
        Assert.Equal(target.Y, position.Y);
        Assert.InRange(((double)walking.RequiredStepHeight), 0.204, 0.206);
    }
}
