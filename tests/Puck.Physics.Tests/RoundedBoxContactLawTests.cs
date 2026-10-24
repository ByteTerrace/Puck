using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class RoundedBoxContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    private static readonly FixedStaticCollider Cube = FixedStaticCollider.RoundedBox(center: FixedVector3.Zero,
        rotation: FixedQuaternion.Identity, innerHalfExtents: V(x: 1.92, y: 1.92, z: 1.92), radius: Q(value: 0.08));

    private static FixedBodyColliderVolume Volume(FixedBodyColliderKind kind) => new(Kind: kind,
        Center: FixedVector3.Zero, Endpoint: V(x: 0, y: 0.8, z: 0), HalfExtents: V(x: 0.01, y: 0.01, z: 0.01),
        Radius: Q(value: 0.01), Rotation: FixedQuaternion.Identity);

    [InlineData(FixedBodyColliderKind.Sphere)]
    [InlineData(FixedBodyColliderKind.Capsule)]
    [InlineData(FixedBodyColliderKind.Box)]
    [Theory]
    public void TheSharpCornerCarriesNothingAndAFlatFaceKeepsItsHeight(FixedBodyColliderKind kind) {
        var volume = Volume(kind: kind);

        Assert.False(condition: Cube.TryGetPush(position: V(x: 2, y: 2.01, z: 2), orientation: FixedQuaternion.Identity,
            volume: volume, skin: FixedQ4816.Zero, push: out _));
        Assert.True(condition: Cube.TryGetPush(position: V(x: 0, y: 2, z: 0), orientation: FixedQuaternion.Identity,
            volume: volume, skin: FixedQ4816.Zero, push: out var face));
        Assert.Equal(FixedVector3.UnitY, face.Normal);
        Assert.InRange(((double)face.Penetration), 0.0099, 0.0101);
    }
    [Fact]
    public void ABodyDropsFromTheOldCornerHeightToTheFillet() {
        var volume = Volume(kind: FixedBodyColliderKind.Sphere);
        var position = V(x: 1.96, y: 2.01, z: 1.96);
        var velocity = V(x: 0, y: -1, z: 0);
        var solver = new FixedStaticContactSolver(ContactSkin: FixedQ4816.Zero, GroundedThreshold: Q(value: 0.5), MaxIterations: 8);

        Assert.False(condition: Cube.TryGetPush(position: position, orientation: FixedQuaternion.Identity,
            volume: volume, skin: FixedQ4816.Zero, push: out _));
        position += V(x: 0, y: -0.03, z: 0);
        var resolution = solver.Resolve(colliders: [Cube], dynamicColliders: [], position: ref position,
            velocity: ref velocity, orientation: FixedQuaternion.Identity, volumes: [volume], up: FixedVector3.UnitY);

        Assert.True(condition: resolution.Grounded);
        Assert.InRange(((double)position.Y), 1.98, 1.997);
        // Independent rounded-cube equation: the centre is body radius + fillet radius from the inner corner.
        Assert.InRange(((double)(position - V(x: 1.92, y: 1.92, z: 1.92)).Length), 0.0899, 0.0901);
    }
    [InlineData(FixedBodyColliderKind.Capsule)]
    [InlineData(FixedBodyColliderKind.Box)]
    [Theory]
    public void RotatedBodiesMeetTheRoundedEdgeAndDeepOverlapStillExits(FixedBodyColliderKind kind) {
        var volume = Volume(kind: kind);
        var rotation = FixedQuaternion.FromAxisAngle(axis: FixedVector3.UnitZ, angle: Q(value: 0.6));

        Assert.True(condition: Cube.TryGetPush(position: V(x: 1.97, y: 1.97, z: 0), orientation: rotation,
            volume: volume, skin: FixedQ4816.Zero, push: out var edge));
        Assert.True(condition: (edge.Normal.X > FixedQ4816.Zero));
        Assert.True(condition: (edge.Normal.Y > FixedQ4816.Zero));
        Assert.True(condition: Cube.TryGetPush(position: FixedVector3.Zero, orientation: rotation,
            volume: volume, skin: FixedQ4816.Zero, push: out var inside));
        Assert.True(condition: (inside.Penetration > FixedQ4816.One));
    }
    [Fact]
    public void SurfaceAndDirectedQueriesReturnTheFilletIncludingAnInteriorProbeAndRotation() {
        var rotation = FixedQuaternion.FromAxisAngle(axis: FixedVector3.UnitY, angle: Q(value: 0.4));
        var turned = Cube with { Rotation = rotation };
        var localProbe = V(x: 2, y: 2, z: 2);
        var probe = rotation.Rotate(vector: localProbe);
        var direction = rotation.Rotate(vector: V(x: -1, y: -1, z: -1));

        Assert.True(condition: FixedSurfaceQuery.TryNearestDirected(colliders: [turned], dynamicColliders: [], origin: probe,
            direction: direction, maxDistance: Q(value: 1), assistHalfAngle: Q(value: 0.01), candidate: out var corner));
        var local = rotation.RotateInverse(vector: corner.Point);

        Assert.InRange(((double)local.X), 1.966, 1.967);
        Assert.InRange(((double)local.Y), 1.966, 1.967);
        Assert.InRange(((double)local.Z), 1.966, 1.967);
        Assert.True(condition: FixedSurfaceQuery.TryNearest(colliders: [Cube], dynamicColliders: [], probe: FixedVector3.Zero,
            reach: Q(value: 3), candidate: out var inside));
        Assert.Equal(V(x: 2, y: 0, z: 0), inside.Point);
        Assert.Equal(FixedVector3.UnitX, inside.Normal);
    }
}
