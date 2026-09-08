using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class ContactKernelTests {
    [Fact]
    public void DynamicSpherePairReturnsTheDeepestRightToLeftCorrection() {
        FixedBodyColliderVolume[] left = [Sphere(radius: 1d)];
        FixedBodyColliderVolume[] right = [Sphere(radius: 1d)];

        var overlaps = FixedDynamicBodyContacts.TryCorrection(
            leftPosition: FixedVector3.Zero,
            leftOrientation: FixedQuaternion.Identity,
            leftVolumes: left,
            rightPosition: Vector(x: 1.5d, y: 0d, z: 0d),
            rightOrientation: FixedQuaternion.Identity,
            rightVolumes: right,
            tieBreaker: 0,
            correction: out var correction
        );

        Assert.True(condition: overlaps);
        Assert.Equal(expected: Vector(x: -0.5d, y: 0d, z: 0d), actual: correction);
        Assert.Equal(expected: Scalar(value: 1d), actual: FixedDynamicBodyContacts.BroadphaseRadius(volumes: left));
    }
    [Fact]
    public void DynamicBoxPairAlongAWorldAxisMatchesTheAxisAlignedOverlap() {
        FixedBodyColliderVolume[] left = [Box(halfExtents: (0.5d, 0.5d, 0.5d))];
        FixedBodyColliderVolume[] right = [Box(halfExtents: (0.5d, 0.5d, 0.5d))];

        var overlaps = FixedDynamicBodyContacts.TryCorrection(
            leftPosition: FixedVector3.Zero,
            leftOrientation: FixedQuaternion.Identity,
            leftVolumes: left,
            rightPosition: Vector(x: 0.9d, y: 0d, z: 0d),
            rightOrientation: FixedQuaternion.Identity,
            rightVolumes: right,
            tieBreaker: 0,
            correction: out var correction
        );

        Assert.True(condition: overlaps);
        Assert.Equal(expected: Vector(x: -0.1d, y: 0d, z: 0d), actual: correction);
    }
    [Fact]
    public void DynamicBoxPairTestsEachBoxsOwnFaceAxisNotOnlyWorldAxes() {
        // Two unit cubes: left axis-aligned at the origin, right rotated 45 degrees about Z and placed 1.3 units
        // out along that same 45-degree diagonal. World-axis-only overlap (X: 1.207-0.919, Y: the same, Z: 1.0)
        // reads positive on every world axis, so a correction restricted to world X/Y/Z axes calls this pair
        // overlapping. The right box's OWN face axis IS that diagonal direction, and its support there (0.5, the
        // half-extent facing the corner squarely) plus the left box's corner-reach support there (root two over
        // two) falls short of the 1.3 separation — the true separating axis a world-axis-only test never tries.
        FixedBodyColliderVolume[] left = [Box(halfExtents: (0.5d, 0.5d, 0.5d))];
        FixedBodyColliderVolume[] right = [Box(halfExtents: (0.5d, 0.5d, 0.5d))];
        var diagonal = FixedQ4816.FromDouble(value: 1.3d);
        var component = (diagonal * FixedQ4816.FromDouble(value: 0.70710678118654752d));

        var overlaps = FixedDynamicBodyContacts.TryCorrection(
            leftPosition: FixedVector3.Zero,
            leftOrientation: FixedQuaternion.Identity,
            leftVolumes: left,
            rightPosition: new FixedVector3(X: component, Y: component, Z: FixedQ4816.Zero),
            rightOrientation: FixedQuaternion.FromAxisAngle(
                axis: Vector(x: 0d, y: 0d, z: 1d),
                angle: FixedQ4816.FromDouble(value: (Math.PI / 4d))
            ),
            rightVolumes: right,
            tieBreaker: 0,
            correction: out _
        );

        Assert.False(condition: overlaps, userMessage: "the diagonal box-box pair must read separated on the tilted box's own face axis, not just world X/Y/Z");
    }
    [Fact]
    public void StaticHalfSpaceReturnsGeometryWithoutApplyingWorldPolicy() {
        var ground = FixedStaticCollider.HalfSpace(
            point: FixedVector3.Zero,
            normal: Vector(x: 0d, y: 1d, z: 0d)
        );
        var volume = Sphere(radius: 1d);
        var orientation = FixedQuaternion.Identity;

        var overlaps = ground.TryGetPush(
            position: Vector(x: 0d, y: 0.5d, z: 0d),
            orientation: in orientation,
            volume: in volume,
            skin: Scalar(value: 0.1d),
            push: out var push
        );

        Assert.True(condition: overlaps);
        Assert.Equal(expected: Vector(x: 0d, y: 1d, z: 0d), actual: push.Normal);
        Assert.Equal(expected: Scalar(value: 0.6d), actual: push.Penetration);
    }
    [Fact]
    public void RigidSolverRefusesAnInvalidIterationBudgetByName() {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
            new FixedRigidSolver(options: new FixedRigidSolverOptions { SolveIterations = 0 }));

        Assert.Equal(expected: nameof(FixedRigidSolverOptions.SolveIterations), actual: exception.ParamName);
    }

    private static FixedBodyColliderVolume Box((double X, double Y, double Z) halfExtents) => new(
        Kind: FixedBodyColliderKind.Box,
        Center: FixedVector3.Zero,
        Endpoint: FixedVector3.Zero,
        HalfExtents: Vector(x: halfExtents.X, y: halfExtents.Y, z: halfExtents.Z),
        Rotation: FixedQuaternion.Identity,
        Radius: FixedQ4816.Zero
    );
    private static FixedBodyColliderVolume Sphere(double radius) => new(
        Kind: FixedBodyColliderKind.Sphere,
        Center: FixedVector3.Zero,
        Endpoint: FixedVector3.Zero,
        HalfExtents: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity,
        Radius: Scalar(value: radius)
    );
    private static FixedQ4816 Scalar(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(X: Scalar(value: x), Y: Scalar(value: y), Z: Scalar(value: z));
}
