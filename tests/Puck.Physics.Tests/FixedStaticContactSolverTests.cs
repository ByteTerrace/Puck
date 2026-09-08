using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedStaticContactSolverTests {
    // cos(45 degrees): a normal must lean no further than that from +Y to ground the body.
    private static readonly FixedQ4816 GroundedThreshold = FixedQ4816.FromDouble(value: 0.70710678d);

    private static FixedStaticContactSolver Solver(int iterations = 4) =>
        new(
            ContactSkin: FixedQ4816.Zero,
            GroundedThreshold: GroundedThreshold,
            MaxIterations: iterations
        );
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    private static FixedBodyColliderVolume UnitSphere() =>
        new(
            Center: FixedVector3.Zero,
            Endpoint: FixedVector3.Zero,
            HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Sphere,
            Radius: FixedQ4816.One,
            Rotation: FixedQuaternion.Identity
        );
    private static FixedStaticCollider Floor(double height) =>
        FixedStaticCollider.HalfSpace(
            normal: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
            point: Vector(
                x: 0d,
                y: height,
                z: 0d
            )
        );

    [Fact]
    public void AFloorGroundsTheBodyAndKillsTheDownwardVelocity() {
        var position = Vector(
            x: 0d,
            y: 0.25d,
            z: 0d
        );
        var velocity = Vector(
            x: 0d,
            y: -4d,
            z: 0d
        );
        FixedBodyColliderVolume[] volumes = [UnitSphere()];
        FixedStaticCollider[] colliders = [Floor(height: 0d)];

        var resolution = Solver().Resolve(
            colliders: colliders,
            dynamicColliders: [],
            orientation: FixedQuaternion.Identity,
            up: Vector(x: 0d, y: 1d, z: 0d),
            position: ref position,
            velocity: ref velocity,
            volumes: volumes
        );

        Assert.True(condition: resolution.Grounded);
        Assert.Equal(
            actual: resolution.ObstructionNormal,
            expected: FixedVector3.Zero
        );
        Assert.Equal(
            actual: position.Y,
            expected: FixedQ4816.One
        );
        Assert.Equal(
            actual: velocity.Y,
            expected: FixedQ4816.Zero
        );
    }
    [Fact]
    public void AVerticalWallPushesAndWitnessesWithoutGrounding() {
        var position = Vector(
            x: 0.25d,
            y: 5d,
            z: 0d
        );
        var velocity = Vector(
            x: -3d,
            y: 0d,
            z: 0d
        );
        FixedBodyColliderVolume[] volumes = [UnitSphere()];
        FixedStaticCollider[] colliders = [
            FixedStaticCollider.HalfSpace(
                normal: Vector(
                    x: 1d,
                    y: 0d,
                    z: 0d
                ),
                point: FixedVector3.Zero
            ),
        ];

        var resolution = Solver().Resolve(
            colliders: colliders,
            dynamicColliders: [],
            orientation: FixedQuaternion.Identity,
            up: Vector(x: 0d, y: 1d, z: 0d),
            position: ref position,
            velocity: ref velocity,
            volumes: volumes
        );

        Assert.False(condition: resolution.Grounded);
        Assert.Equal(
            actual: resolution.ObstructionNormal,
            expected: Vector(
                x: 1d,
                y: 0d,
                z: 0d
            )
        );
        Assert.Equal(
            actual: position.X,
            expected: FixedQ4816.One
        );
        Assert.Equal(
            actual: velocity.X,
            expected: FixedQ4816.Zero
        );
    }
    // The two spans interleave within one iteration, so a caller may split "compiled once" from "rebuilt per tick"
    // without changing the result.
    [Fact]
    public void BothSpansResolveInTheSameCall() {
        FixedBodyColliderVolume[] volumes = [UnitSphere()];
        FixedStaticCollider[] floor = [Floor(height: 0d)];
        FixedStaticCollider[] wall = [
            FixedStaticCollider.HalfSpace(
                normal: Vector(
                    x: 1d,
                    y: 0d,
                    z: 0d
                ),
                point: FixedVector3.Zero
            ),
        ];
        var split = Vector(
            x: 0.25d,
            y: 0.25d,
            z: 0d
        );
        var splitVelocity = FixedVector3.Zero;
        var combined = split;
        var combinedVelocity = FixedVector3.Zero;

        var splitResolution = Solver().Resolve(
            colliders: floor,
            dynamicColliders: wall,
            orientation: FixedQuaternion.Identity,
            up: Vector(x: 0d, y: 1d, z: 0d),
            position: ref split,
            velocity: ref splitVelocity,
            volumes: volumes
        );
        var combinedResolution = Solver().Resolve(
            colliders: [.. floor, .. wall],
            dynamicColliders: [],
            orientation: FixedQuaternion.Identity,
            up: Vector(x: 0d, y: 1d, z: 0d),
            position: ref combined,
            velocity: ref combinedVelocity,
            volumes: volumes
        );

        Assert.Equal(
            actual: combined,
            expected: split
        );
        Assert.Equal(
            actual: combinedResolution,
            expected: splitResolution
        );
        Assert.True(condition: splitResolution.Grounded);
    }
    [Fact]
    public void NoColliderLeavesTheBodyUntouched() {
        var position = Vector(
            x: 1d,
            y: 5d,
            z: -2d
        );
        var velocity = Vector(
            x: 0d,
            y: -9d,
            z: 0d
        );
        FixedBodyColliderVolume[] volumes = [UnitSphere()];

        var resolution = Solver().Resolve(
            colliders: [],
            dynamicColliders: [],
            orientation: FixedQuaternion.Identity,
            up: Vector(x: 0d, y: 1d, z: 0d),
            position: ref position,
            velocity: ref velocity,
            volumes: volumes
        );

        Assert.False(condition: resolution.Grounded);
        Assert.Equal(
            actual: position,
            expected: Vector(
                x: 1d,
                y: 5d,
                z: -2d
            )
        );
        Assert.Equal(
            actual: velocity.Y,
            expected: FixedQ4816.FromDouble(value: -9d)
        );
    }
}
