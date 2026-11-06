using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedStaticContactSolverTests {
    // cos(45 degrees): a normal must lean no further than that from +Y to ground the body.
    private static readonly FixedQ4816 GroundedThreshold = FixedQ4816.FromDouble(value: 0.70710678d);

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
    // The per-tick colliders, as a caller that recomputes them each tick publishes them.
    private static FixedStaticColliderSet Refreshed(FixedStaticCollider[] colliders) {
        var set = new FixedStaticColliderSet();

        set.Refresh(colliders: colliders);
        return set;
    }
    private static FixedStaticContactSolver Solver(int iterations = 4) =>
        new(
            ContactSkin: FixedQ4816.Zero,
            GroundedThreshold: GroundedThreshold,
            MaxIterations: iterations
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
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
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
            colliders: new(colliders: colliders),
            dynamicColliders: FixedStaticColliderSet.Empty,
            orientation: FixedQuaternion.Identity,
            up: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
            position: ref position,
            velocity: ref velocity,
            volumes: volumes
        );

        Assert.True(condition: resolution.Grounded);
        var support = Assert.IsType<ContactSupportWitness>(@object: resolution.Support);

        Assert.Equal(colliders[0], support.Collider);
        Assert.Equal(FixedSurfaceColliderSource.Static, support.Source);
        Assert.Equal(0, support.ColliderIndex);
        Assert.Equal(position, support.Position);
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
            colliders: new(colliders: colliders),
            dynamicColliders: FixedStaticColliderSet.Empty,
            orientation: FixedQuaternion.Identity,
            up: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
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
            colliders: new(colliders: floor),
            dynamicColliders: Refreshed(colliders: wall),
            orientation: FixedQuaternion.Identity,
            up: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
            position: ref split,
            velocity: ref splitVelocity,
            volumes: volumes
        );
        var combinedResolution = Solver().Resolve(
            colliders: new(colliders: [.. floor, .. wall]),
            dynamicColliders: FixedStaticColliderSet.Empty,
            orientation: FixedQuaternion.Identity,
            up: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
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
            colliders: FixedStaticColliderSet.Empty,
            dynamicColliders: FixedStaticColliderSet.Empty,
            orientation: FixedQuaternion.Identity,
            up: Vector(
                x: 0d,
                y: 1d,
                z: 0d
            ),
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
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void FastMotionStopsAtAThinWallOnlyWhenOneExists(bool wall) {
        var start = Vector(x: -3d, y: 1d, z: 0d);
        var position = Vector(x: 3d, y: 1d, z: 0d);
        var velocity = Vector(x: 30d, y: 0d, z: 0d);
        FixedStaticCollider[] colliders = [FixedStaticCollider.AxisAlignedBox(
            center: Vector(x: (wall ? 0d : 20d), y: 1d, z: 0d), halfExtents: Vector(x: 0.05d, y: 2d, z: 2d))];
        FixedBodyColliderVolume[] volumes = [UnitSphere() with { Radius = FixedQ4816.FromDouble(value: 0.25d) }];
        var result = Solver().ResolveSweep(colliders: new(colliders: colliders), dynamicColliders: FixedStaticColliderSet.Empty, previousPosition: start,
            position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity, volumes: volumes, up: FixedVector3.UnitY);

        Assert.Equal(expected: ContactRefusal.None, actual: result.Refusal);
        Assert.False(condition: result.Unproved);
        if (wall) {
            Assert.True(condition: (position.X < FixedQ4816.Zero));
            Assert.Equal(expected: -FixedVector3.UnitX, actual: result.ObstructionNormal);
            Assert.Equal(expected: FixedQ4816.Zero, actual: velocity.X);
        } else {
            Assert.Equal(expected: Vector(x: 3d, y: 1d, z: 0d), actual: position);
            Assert.Equal(expected: FixedVector3.Zero, actual: result.ObstructionNormal);
            Assert.Equal(expected: FixedQ4816.FromInteger(value: 30), actual: velocity.X);
        }
    }
    [Fact]
    public void ExhaustedAnalyticSweepKeepsOnlyItsProvedPrefix() {
        var start = Vector(x: 0d, y: 1d, z: 0d);
        var position = Vector(x: 1000d, y: 1d, z: 0d);
        var velocity = Vector(x: 30000d, y: 0d, z: 0d);
        FixedStaticCollider[] colliders = [FixedStaticCollider.AxisAlignedBox(center: Vector(x: 500d, y: 1d, z: 0d),
            halfExtents: Vector(x: 0.05d, y: 2d, z: 2d))];
        FixedBodyColliderVolume[] volumes = [UnitSphere() with { Radius = FixedQ4816.FromDouble(value: 0.25d) }];
        var result = Solver().ResolveSweep(colliders: new(colliders: colliders), dynamicColliders: FixedStaticColliderSet.Empty, previousPosition: start,
            position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity, volumes: volumes, up: FixedVector3.UnitY);

        Assert.True(condition: result.Unproved);
        Assert.Equal(expected: ContactRefusal.None, actual: result.Refusal);
        Assert.Equal(expected: FixedVector3.Zero, actual: result.ObstructionNormal);
        Assert.True(condition: (position.X > start.X));
        Assert.True(condition: (position.X < FixedQ4816.FromInteger(value: 500)));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 30000), actual: velocity.X);
    }
    // A small, fast body crossing space no collider's bounds reach arrives at its endpoint proved.
    [Fact]
    public void AMoveThroughClearSpaceReachesItsEndpointInOneSolve() {
        var start = Vector(x: 0d, y: 1d, z: 0d);
        var end = Vector(x: 2d, y: 1d, z: 0d);
        var position = end;
        var velocity = Vector(x: 60d, y: 0d, z: 0d);
        FixedStaticCollider[] colliders = [FixedStaticCollider.AxisAlignedBox(center: Vector(x: 1d, y: 1d, z: 50d),
            halfExtents: Vector(x: 0.05d, y: 2d, z: 2d))];
        FixedBodyColliderVolume[] volumes = [UnitSphere() with { Radius = FixedQ4816.FromDouble(value: 0.01d) }];
        var result = Solver().ResolveSweep(colliders: new(colliders: colliders), dynamicColliders: FixedStaticColliderSet.Empty, previousPosition: start,
            position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity, volumes: volumes,
            up: FixedVector3.UnitY);

        Assert.False(condition: result.Unproved);
        Assert.Equal(expected: end, actual: position);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 60), actual: velocity.X);
    }

}
