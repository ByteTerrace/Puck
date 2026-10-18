using Puck.Maths;

namespace Puck.Physics.Tests;

// A thin wall yawed 45° has a world-axis bounding box about 1.5 m square; these laws pin that a body meets the wall
// itself, along its own normal, and nothing of that bounding box.
public sealed class OrientedBoxContactLawTests {
    private static readonly FixedQ4816 Tolerance = FixedQ4816.FromDouble(value: 0.0002d);
    private static readonly FixedQuaternion Yaw = FixedQuaternion.FromAxisAngle(
        angle: FixedQ4816.FromDouble(value: (Math.PI / 4d)),
        axis: FixedVector3.UnitY
    );
    private static readonly FixedVector3 WallCenter = Vector(x: 0d, y: 1d, z: 0d);
    private static readonly FixedStaticCollider Wall = FixedStaticCollider.Box(
        center: WallCenter,
        halfExtents: Vector(x: 2d, y: 1d, z: 0.05d),
        rotation: Yaw
    );
    private static readonly FixedVector3 WallNormal = Yaw.Rotate(vector: FixedVector3.UnitZ);
    private static readonly FixedBodyColliderVolume Capsule = new(
        Center: Vector(x: 0d, y: 0.35d, z: 0d),
        Endpoint: Vector(x: 0d, y: 1.71d, z: 0d),
        HalfExtents: FixedVector3.Zero,
        Kind: FixedBodyColliderKind.Capsule,
        Radius: FixedQ4816.FromDouble(value: 0.35d),
        Rotation: FixedQuaternion.Identity
    );

    private static void AssertNear(FixedVector3 expected, FixedVector3 actual) =>
        Assert.True(
            condition: ((FixedQ4816.Abs(value: (expected.X - actual.X)) <= Tolerance) &&
                        (FixedQ4816.Abs(value: (expected.Y - actual.Y)) <= Tolerance) &&
                        (FixedQ4816.Abs(value: (expected.Z - actual.Z)) <= Tolerance)),
            userMessage: $"expected {expected.ToVector3()}, got {actual.ToVector3()}"
        );
    private static FixedVector3 Foot(double offset) => (WallNormal * FixedQ4816.FromDouble(value: offset));
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

    [Fact]
    public void AnUnturnedBoxKeepsTheAxisAlignedCollider() {
        var halfExtents = Vector(x: 2d, y: 1d, z: 0.05d);

        Assert.Equal(
            actual: FixedStaticCollider.Box(center: WallCenter, halfExtents: halfExtents, rotation: FixedQuaternion.Identity),
            expected: FixedStaticCollider.AxisAlignedBox(center: WallCenter, halfExtents: halfExtents)
        );
        Assert.Equal(actual: Wall.Kind, expected: FixedStaticColliderKind.OrientedBox);
    }
    [Fact]
    public void ACapsuleIsPushedAlongTheYawedWallsNormal() {
        Assert.True(condition: Wall.TryGetPush(
            orientation: FixedQuaternion.Identity,
            position: Foot(offset: 0.3d),
            push: out var push,
            skin: FixedQ4816.Zero,
            volume: Capsule
        ));
        AssertNear(actual: push.Normal, expected: WallNormal);
        Assert.InRange(actual: ((double)push.Penetration), high: 0.1002d, low: 0.0998d);
    }
    [Fact]
    public void ACapsuleOutsideTheWallButInsideItsBoundingBoxIsClear() {
        var foot = Foot(offset: 0.5d);
        var velocity = (WallNormal * -FixedQ4816.One);
        var resolved = foot;

        // The foot sits about 0.35 m inside the wall's world-axis bounds on both X and Z.
        Assert.InRange(actual: ((double)foot.X), high: 1.4d, low: 0.3d);
        Assert.False(condition: Wall.TryGetPush(
            orientation: FixedQuaternion.Identity,
            position: foot,
            push: out _,
            skin: FixedQ4816.Zero,
            volume: Capsule
        ));
        _ = new FixedStaticContactSolver(ContactSkin: FixedQ4816.Zero, GroundedThreshold: FixedQ4816.FromDouble(value: 0.5d), MaxIterations: 4).Resolve(
            colliders: [Wall],
            dynamicColliders: [],
            orientation: FixedQuaternion.Identity,
            position: ref resolved,
            up: FixedVector3.UnitY,
            velocity: ref velocity,
            volumes: [Capsule]
        );
        Assert.Equal(actual: resolved, expected: foot);
    }
    [Fact]
    public void TheNearestSurfaceOnTheYawedWallIsItsFace() {
        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            candidate: out var candidate,
            colliders: [Wall],
            dynamicColliders: [],
            probe: (WallCenter + Foot(offset: 0.5d)),
            reach: FixedQ4816.One
        ));
        AssertNear(actual: candidate.Normal, expected: WallNormal);
        AssertNear(actual: candidate.Point, expected: (WallCenter + Foot(offset: 0.05d)));
    }
}
