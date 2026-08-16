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
