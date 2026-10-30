using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>A field collider measures its surface: a field that reads a fixed multiple of a sphere's distance (as a
/// relieved creation's scoped field reads a fraction of its own) must still hold every body volume exactly the skin
/// outside the sphere, along its radial normal, in any frame.</summary>
public sealed class FieldColliderContactLawTests {
    // F(p) = gain·(|p| − radius): its zero set is the sphere, and value over gradient length is the exact distance.
    private sealed class ScaledSphereField(FixedQ4816 radius, FixedQ4816 gain) : IFieldEvaluator {
        public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            material = 0;
            distance = FixedQ4816.Zero;
            if (!position.TryDelta(delta: out var point, origin: FixedPosition.Zero)) { return false; }
            distance = (gain * (point.Length - radius));
            return true;
        }
        public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => throw new NotSupportedException();
        public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) => throw new NotSupportedException();
    }

    private static readonly FixedQ4816 Radius = Q(value: 10d);
    private static readonly FixedQ4816 Skin = Q(value: 0.05d);

    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static FixedStaticCollider Sphere(FixedVector3 center, FixedQuaternion rotation) => FixedStaticCollider.FieldSurface(
        center: center,
        field: new FixedColliderField(boundScale: Q(value: 0.5d), field: new ScaledSphereField(gain: Q(value: 0.5d), radius: Radius), gradientStep: Q(value: 1d / 32d)),
        halfExtent: V(x: 10d, y: 10d, z: 10d),
        rotation: rotation
    );
    private static FixedBodyColliderVolume Capsule() => new(Center: V(x: 0d, y: 0.35d, z: 0d), Endpoint: V(x: 0d, y: 1.7d, z: 0d),
        HalfExtents: FixedVector3.Zero, Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity);
    private static void Near(double expected, FixedQ4816 actual, double tolerance = 2e-3) =>
        Assert.InRange(actual: (double)actual, high: (expected + tolerance), low: (expected - tolerance));

    [Fact]
    public void ACapsuleSinkingIntoTheSurfaceIsPushedOutAlongTheRadiusToTheSkin() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // The foot 0.1 m inside the sphere's top: the lower core point is 0.25 m above the surface.
        var position = V(x: 0d, y: 9.9d, z: 0d);

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.15d);
        Near(actual: push.Normal.Y, expected: 1d);
        Assert.False(condition: push.IsEdge);
    }

    [Fact]
    public void ACapsuleUnderTheSurfaceIsPushedDownFromItsUpperEnd() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // The upper core point 0.25 m under the sphere's bottom.
        var position = V(x: 0d, y: -11.95d, z: 0d);

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.15d);
        Near(actual: push.Normal.Y, expected: -1d);
    }

    [Fact]
    public void ACapsuleAgainstTheSideTouchesAtItsNearestCorePoint() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // Upright beside the sphere's equator: the core point level with the centre is nearest, 0.3 m out.
        var position = V(x: 10.3d, y: -1d, z: 0d);

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.1d, tolerance: 0.01d);
        Near(actual: push.Normal.X, expected: 1d, tolerance: 0.01d);
    }

    [Fact]
    public void ABodyTheBoundScaleProvesClearTakesOneSampleAndNoPush() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var samples = FixedStaticContactWork.Process.Read(kind: FixedStaticContactWork.FieldSamples);
        var queries = FixedStaticContactWork.Process.Read(kind: FixedStaticContactWork.FieldQueries);

        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 20d, z: 0d), push: out _, skin: Skin, volume: Capsule()));
        // Other tests may count into the process ledger at the same time, so the window bounds this query from below.
        Assert.True(condition: (FixedStaticContactWork.Process.Read(kind: FixedStaticContactWork.FieldQueries) > queries));
        Assert.True(condition: (FixedStaticContactWork.Process.Read(kind: FixedStaticContactWork.FieldSamples) > samples));
    }

    [Fact]
    public void AnEnvelopeProvesAFarBodyClearAndLeavesANearOneToTheField() {
        var plain = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var collider = (plain with { Field = new FixedColliderField(boundScale: plain.Field!.BoundScale, field: plain.Field.Field, gradientStep: plain.Field.GradientStep) {
            Envelope = new ScaledSphereField(gain: FixedQ4816.One, radius: Radius),
            EnvelopeGrowth = Q(value: 0.1d),
        } });
        var box = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);

        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 20d, z: 0d), push: out _, skin: Skin, volume: Capsule()));
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 20d, z: 0d), push: out _, skin: Skin, volume: box));
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 9.9d, z: 0d), push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.15d);
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.2d, z: 0d), push: out var boxPush, skin: Skin, volume: box));
        Near(actual: boxPush.Penetration, expected: 0.1d);
    }

    [Fact]
    public void ABoxAndAnEllipsoidRestTheirSupportTheSkinOutside() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var box = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);
        var ellipsoid = (box with { Kind = FixedBodyColliderKind.Ellipsoid });

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.2d, z: 0d), push: out var boxPush, skin: Skin, volume: box));
        Near(actual: boxPush.Penetration, expected: 0.1d);
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.2d, z: 0d), push: out var ellipsoidPush, skin: Skin, volume: ellipsoid));
        Near(actual: ellipsoidPush.Penetration, expected: 0.1d);
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.31d, z: 0d), push: out _, skin: Skin, volume: box));
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 40d, z: 0d), push: out _, skin: Skin, volume: box));
    }

    [Fact]
    public void AMovedAndTurnedFrameCarriesTheSurfaceAndItsNormal() {
        var turn = FixedQuaternion.FromAxisAngle(angle: FixedQ4816.HalfPi, axis: FixedVector3.UnitZ);
        var collider = Sphere(center: V(x: 100d, y: 0d, z: 0d), rotation: turn);
        var sphere = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Sphere, Radius: Q(value: 0.5d), Rotation: FixedQuaternion.Identity);

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 100d, y: 10.4d, z: 0d), push: out var push, skin: Skin, volume: sphere));
        Near(actual: push.Penetration, expected: 0.15d);
        Near(actual: push.Normal.Y, expected: 1d);

        var probe = V(x: 100d, y: 0d, z: 13d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(candidate: out var nearest, colliders: [collider], dynamicColliders: [], probe: in probe, reach: Q(value: 5d)));
        Near(actual: nearest.Point.Z, expected: 10d);
        Near(actual: nearest.Normal.Z, expected: 1d);
        Near(actual: nearest.Distance, expected: 3d);
        Assert.True(condition: FixedFieldColliderContact.TryMeasure(collider: in collider, distance: out var distance, normal: out _, point: V(x: 100d, y: -12d, z: 0d)));
        Near(actual: distance, expected: 2d);
    }

    [Fact]
    public void TheSolverStandsABodyOnTheFieldAndGroundsIt() {
        FixedStaticCollider[] colliders = [Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity)];
        var solver = new FixedStaticContactSolver(ContactSkin: Skin, GroundedThreshold: Q(value: 0.7071d), MaxIterations: 4);
        var position = V(x: 0d, y: 9.95d, z: 0d);
        var velocity = V(x: 0d, y: -1d, z: 0d);

        var resolution = solver.Resolve(colliders: colliders, dynamicColliders: [], orientation: FixedQuaternion.Identity, position: ref position,
            up: FixedVector3.UnitY, velocity: ref velocity, volumes: [Capsule()]);

        Assert.True(condition: resolution.Grounded);
        Near(actual: position.Y, expected: 10.05d);
        Assert.Equal(expected: FixedQ4816.Zero, actual: velocity.Y);
    }

    [Fact]
    public void AFieldWithoutDirectionOrAnswerPushesNothing() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var sphere = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Sphere, Radius: Q(value: 0.5d), Rotation: FixedQuaternion.Identity);

        // At the sphere's centre every tap reads alike: no measured normal, no push, and no projection step.
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Skin, volume: sphere));
        Assert.False(condition: FixedFieldColliderContact.TryMeasure(collider: in collider, distance: out _, normal: out var none, point: FixedVector3.Zero));
        Assert.Equal(expected: FixedVector3.Zero, actual: none);
        Assert.Equal(expected: (FixedVector3.Zero, FixedVector3.UnitY), actual: FixedFieldColliderContact.Nearest(collider: in collider, probe: FixedVector3.Zero));
    }

    // A field that answers only on one plane of y, reading zero there.
    private sealed class PlaneOnlyField(FixedQ4816 y) : IFieldEvaluator {
        public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            material = 0;
            distance = FixedQ4816.Zero;
            return (position.TryDelta(delta: out var point, origin: FixedPosition.Zero) && (point.Y == y));
        }
        public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => throw new NotSupportedException();
        public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) => throw new NotSupportedException();
    }

    [Fact]
    public void AFieldThatDoesNotAnswerPushesNothing() {
        // The capsule's core runs 0.35 to 1.7 m: its middle at 1.025 m answers, none of its three spaced samples do.
        var collider = FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: new FixedColliderField(boundScale: FixedQ4816.One,
            field: new PlaneOnlyField(y: Q(value: 1.025d)), gradientStep: Q(value: 1d / 32d)), halfExtent: V(x: 1d, y: 1d, z: 1d), rotation: FixedQuaternion.Identity);
        var wide = (Capsule() with { Radius = Q(value: 0.5d) });
        var box = new FixedBodyColliderVolume(Center: V(x: 0d, y: 3d, z: 0d), Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);

        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Skin, volume: wide));
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Skin, volume: box));
        Assert.False(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: -1.025d, z: 0d), push: out _, skin: Skin, volume: (box with { Center = V(x: 0d, y: 2.05d, z: 0d) })));
    }

    [Fact]
    public void AFieldColliderHasNoSupportMapAndAnUnboundedOneNoBounds() {
        var bounded = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var unbounded = (bounded with { Field = new FixedColliderField(boundScale: FixedQ4816.One, bounded: false, field: bounded.Field!.Field, gradientStep: Q(value: 0.01d)) });

        Assert.NotNull(@object: bounded.WorldHalfExtent());
        Assert.Null(@object: unbounded.WorldHalfExtent());
        Assert.Throws<InvalidOperationException>(testCode: () => bounded.SupportPoint(direction: FixedVector3.UnitX));
        Assert.Throws<InvalidOperationException>(testCode: () => (bounded with { Field = null }).TryGetPush(orientation: FixedQuaternion.Identity,
            position: FixedVector3.Zero, push: out _, skin: Skin, volume: Capsule()));
        Assert.Throws<ArgumentNullException>(testCode: () => FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: null!, halfExtent: FixedVector3.Zero, rotation: FixedQuaternion.Identity));
        Assert.Throws<ArgumentNullException>(testCode: () => new FixedColliderField(boundScale: FixedQ4816.One, field: null!, gradientStep: FixedQ4816.One));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new FixedColliderField(boundScale: FixedQ4816.Zero, field: bounded.Field.Field, gradientStep: FixedQ4816.One));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new FixedColliderField(boundScale: FixedQ4816.One, field: bounded.Field.Field, gradientStep: FixedQ4816.Zero));
        Assert.Empty(collection: bounded.Field.Bases);
    }
}
