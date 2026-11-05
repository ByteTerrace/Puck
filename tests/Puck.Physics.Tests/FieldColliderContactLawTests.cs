using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests;

/// <summary>A field collider measures its surface: a field that reads a fixed multiple of a sphere's distance (as a
/// relieved creation's scoped field reads a fraction of its own) must still hold every body volume exactly the skin
/// outside the sphere, along its radial normal, in any frame.</summary>
public sealed class FieldColliderContactLawTests {
    [Fact]
    public void APositiveFieldScaleDoesNotEraseAMeasurableContact() {
        var collider = FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity,
            halfExtent: V(x: 10d, y: 10d, z: 10d), field: new FixedColliderField(
                field: new ScaledSphereField(radius: Radius, gain: Q(value: 1d / 1024d)),
                gradientStep: Q(value: 1d / 32d)));
        var sphere = Capsule() with { Kind = FixedBodyColliderKind.Sphere, Center = FixedVector3.Zero, Radius = Q(value: 0.5d) };

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.25d, z: 0d),
            push: out var push, skin: Skin, volume: sphere));
        // One raw of this field's value spans 1024 raws of distance, so its zero set lies within half of that, 1/128 m,
        // of the sphere.
        Near(expected: 0.3d, actual: push.Penetration, tolerance: (1d / 128d));
    }

    [Fact]
    public void AWalkableLimitThatRoundsToZeroDoesNotLiftAlongAVerticalFace() {
        var threshold = FixedQ4816.Cos(angle: FixedQ4816.FromDegrees(degrees: 89.9999f));
        Assert.Equal(expected: FixedQ4816.Zero, actual: threshold);
        var velocity = -FixedVector3.UnitX;
        var trial = FixedContactPushMath.ComputeOrdinary(normal: FixedVector3.UnitX, penetration: Skin,
            velocity: in velocity, up: FixedVector3.UnitY, groundedThreshold: threshold);

        Assert.False(condition: trial.Grounded);
        Assert.Equal(expected: FixedVector3.UnitX * Skin, actual: trial.PositionDelta);
        Assert.Equal(expected: FixedVector3.UnitX, actual: trial.VelocityDelta);

        var solver = new FixedStaticContactSolver(ContactSkin: Skin, GroundedThreshold: threshold, MaxIterations: 1);
        var position = V(x: 0.1d, y: 0d, z: 0d);
        var resolution = solver.Resolve(colliders: new(colliders: [FixedStaticCollider.HalfSpace(point: FixedVector3.Zero, normal: FixedVector3.UnitX)]),
            dynamicColliders: FixedStaticColliderSet.Empty, position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity,
            volumes: [Capsule() with { Kind = FixedBodyColliderKind.Sphere, Center = FixedVector3.Zero }], up: FixedVector3.UnitY);
        Assert.False(condition: resolution.Grounded);
        Assert.Null(@object: resolution.Support);
    }

    [Fact]
    public void AFloorNormalAtTheFootDoesNotHideContactAtTheHead() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint().Plane(material: material, normal: -Vector3.UnitY, offset: 2f);
        var field = new SdfFieldEvaluator(program: builder.Build());
        var collider = FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity,
            halfExtent: FixedVector3.Zero, field: new FixedColliderField(field: field,
                gradientStep: Q(value: 1d / 32d), bounded: false));

        // The foot sphere clears y=0 by 0.2 m, while the head sphere crosses y=2 by 0.25 m.
        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 0.2d, z: 0d),
            push: out var push, skin: Skin, volume: Capsule()));
        Near(expected: -1d, actual: push.Normal.Y);
        Near(expected: 0.3d, actual: push.Penetration);
    }

    // F(p) = gain·(|p| − radius): its zero set is the sphere, and value over gradient length is the exact distance. Its
    // box bounds read the least and greatest distance from the origin to the box, widened a raw past the point rule's
    // roundings.
    private sealed class ScaledSphereField(FixedQ4816 radius, FixedQ4816 gain) : IFieldEvaluator, IFieldBounds {
        public int Samples { get; private set; }
        public int BoxReads { get; private set; }
        public FieldEvaluatorCapabilities Capabilities => new(StepScale: FixedQ4816.One, WarpFree: true);
        public FixedQ4816 StepScale => FixedQ4816.One;

        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            distance = default;
            if (!lower.TryDelta(delta: out var low, origin: FixedPosition.Zero) || !upper.TryDelta(delta: out var high, origin: FixedPosition.Zero)) { return false; }
            BoxReads++;
            static double Near(double a, double b) => ((a > 0d) ? a : ((b < 0d) ? -b : 0d));
            static double Far(double a, double b) => Math.Max(val1: Math.Abs(value: a), val2: Math.Abs(value: b));
            var (lx, ly, lz, hx, hy, hz) = ((double)low.X, (double)low.Y, (double)low.Z, (double)high.X, (double)high.Y, (double)high.Z);
            var near = Math.Sqrt(d: ((Math.Pow(x: Near(a: lx, b: hx), y: 2d) + Math.Pow(x: Near(a: ly, b: hy), y: 2d)) + Math.Pow(x: Near(a: lz, b: hz), y: 2d)));
            var far = Math.Sqrt(d: ((Math.Pow(x: Far(a: lx, b: hx), y: 2d) + Math.Pow(x: Far(a: ly, b: hy), y: 2d)) + Math.Pow(x: Far(a: lz, b: hz), y: 2d)));
            var slack = (2d / 65536d);
            distance = new FixedInterval(lower: FixedQ4816.FromDouble(value: (((double)gain * (near - (double)radius)) - slack)),
                upper: FixedQ4816.FromDouble(value: (((double)gain * (far - (double)radius)) + slack)));
            return true;
        }

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            material = 0;
            distance = FixedQ4816.Zero;
            if (!position.TryDelta(delta: out var point, origin: FixedPosition.Zero)) { return false; }
            Samples++;
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
    private static FixedStaticCollider Sphere(FixedVector3 center, FixedQuaternion rotation) {
        var field = new ScaledSphereField(gain: Q(value: 0.5d), radius: Radius);

        return FixedStaticCollider.FieldSurface(center: center, field: new FixedColliderField(field: field, gradientStep: Q(value: 1d / 32d)) { Bounds = field },
            halfExtent: V(x: 10d, y: 10d, z: 10d), rotation: rotation);
    }
    private static FixedBodyColliderVolume Capsule() => new(Center: V(x: 0d, y: 0.35d, z: 0d), Endpoint: V(x: 0d, y: 1.7d, z: 0d),
        HalfExtents: FixedVector3.Zero, Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity);
    private static void Near(double expected, FixedQ4816 actual, double tolerance = 2e-3) =>
        Assert.InRange(actual: (double)actual, high: (expected + tolerance), low: (expected - tolerance));

    [Fact]
    public void ACapsuleSinkingIntoTheSurfaceIsPushedOutAlongTheRadiusToTheSkin() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // The foot 0.1 m inside the sphere's top: the lower core point is 0.25 m above the surface.
        var position = V(x: 0d, y: 9.9d, z: 0d);

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.15d);
        Near(actual: push.Normal.Y, expected: 1d);
        Assert.False(condition: push.IsEdge);
    }

    [Fact]
    public void ACapsuleUnderTheSurfaceIsPushedDownFromItsUpperEnd() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // The upper core point 0.25 m under the sphere's bottom.
        var position = V(x: 0d, y: -11.95d, z: 0d);

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.15d);
        Near(actual: push.Normal.Y, expected: -1d);
    }

    [Fact]
    public void ACapsuleAgainstTheSideTouchesAtItsNearestCorePoint() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        // Upright beside the sphere's equator: the core point level with the centre is nearest, 0.3 m out.
        var position = V(x: 10.3d, y: -1d, z: 0d);

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: position, push: out var push, skin: Skin, volume: Capsule()));
        Near(actual: push.Penetration, expected: 0.1d, tolerance: 0.01d);
        Near(actual: push.Normal.X, expected: 1d, tolerance: 0.01d);
    }

    [Fact]
    public void BoxBoundsProveAFarBodyClearWithoutReadingTheField() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var field = Assert.IsType<ScaledSphereField>(@object: collider.Field!.Field);
        var box = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);

        Assert.Equal(expected: FixedFieldContactOutcome.Clear, actual: FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity,
            position: V(x: 0d, y: 20d, z: 0d), skin: Skin, volume: Capsule()).Outcome);
        Assert.Equal(expected: FixedFieldContactOutcome.Clear, actual: FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity,
            position: V(x: 0d, y: 20d, z: 0d), skin: Skin, volume: box).Outcome);
        Assert.Equal(expected: 0, actual: field.Samples);
        Assert.Equal(expected: 2, actual: field.BoxReads);

        // Without bounds nothing is proved clear: the far body is unproved, never clear.
        var unbounded = (collider with { Field = new FixedColliderField(field: field, gradientStep: collider.Field.GradientStep) });

        Assert.Equal(expected: FixedFieldContactOutcome.Unproved, actual: FixedFieldColliderContact.Solve(collider: in unbounded,
            orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 20d, z: 0d), skin: Skin, volume: Capsule()).Outcome);
    }

    [Fact]
    public void ABoxAndAnEllipsoidRestTheirSupportTheSkinOutside() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var box = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);
        var ellipsoid = (box with { Kind = FixedBodyColliderKind.Ellipsoid });

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.2d, z: 0d), push: out var boxPush, skin: Skin, volume: box));
        Near(actual: boxPush.Penetration, expected: 0.1d);
        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.2d, z: 0d), push: out var ellipsoidPush, skin: Skin, volume: ellipsoid));
        Near(actual: ellipsoidPush.Penetration, expected: 0.1d);
        Assert.False(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 10.31d, z: 0d), push: out _, skin: Skin, volume: box));
        Assert.False(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: 40d, z: 0d), push: out _, skin: Skin, volume: box));
    }

    [Fact]
    public void AMovedAndTurnedFrameCarriesTheSurfaceAndItsNormal() {
        var turn = FixedQuaternion.FromAxisAngle(angle: FixedQ4816.HalfPi, axis: FixedVector3.UnitZ);
        var collider = Sphere(center: V(x: 100d, y: 0d, z: 0d), rotation: turn);
        var sphere = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Sphere, Radius: Q(value: 0.5d), Rotation: FixedQuaternion.Identity);

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 100d, y: 10.4d, z: 0d), push: out var push, skin: Skin, volume: sphere));
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

        var resolution = solver.Resolve(colliders: new(colliders: colliders), dynamicColliders: FixedStaticColliderSet.Empty, orientation: FixedQuaternion.Identity, position: ref position,
            up: FixedVector3.UnitY, velocity: ref velocity, volumes: [Capsule()]);

        Assert.True(condition: resolution.Grounded);
        Near(actual: position.Y, expected: 10.05d);
        Assert.Equal(expected: FixedQ4816.Zero, actual: velocity.Y);
    }

    [Fact]
    public void ASymmetricFieldPointHasNoMeasuredNormal() {
        var collider = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);

        // At the sphere's centre every tap reads alike. This proves no normal, not clearance of an embedded body.
        Assert.False(condition: FixedFieldColliderContact.TryMeasure(collider: in collider, distance: out _, normal: out var none, point: FixedVector3.Zero));
        Assert.Equal(expected: FixedVector3.Zero, actual: none);
    }

    // A field that answers only on one plane of y, reading zero there.
    private sealed class PlaneOnlyField(FixedQ4816 y) : IFieldEvaluator {
        public FieldEvaluatorCapabilities Capabilities => new(StepScale: FixedQ4816.One, WarpFree: true);

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
        var collider = FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: new FixedColliderField(
            field: new PlaneOnlyField(y: Q(value: 1.025d)), gradientStep: Q(value: 1d / 32d)), halfExtent: V(x: 1d, y: 1d, z: 1d), rotation: FixedQuaternion.Identity);
        var wide = (Capsule() with { Radius = Q(value: 0.5d) });
        var box = new FixedBodyColliderVolume(Center: V(x: 0d, y: 3d, z: 0d), Endpoint: FixedVector3.Zero, HalfExtents: V(x: 0.5d, y: 0.25d, z: 0.5d),
            Kind: FixedBodyColliderKind.Box, Radius: FixedQ4816.Zero, Rotation: FixedQuaternion.Identity);

        Assert.False(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Skin, volume: wide));
        Assert.False(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Skin, volume: box));
        Assert.False(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: -1.025d, z: 0d), push: out _, skin: Skin, volume: (box with { Center = V(x: 0d, y: 2.05d, z: 0d) })));
    }

    [Fact]
    public void AFieldColliderHasNoSupportMapAndAnUnboundedOneNoBounds() {
        var bounded = Sphere(center: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
        var unbounded = (bounded with { Field = new FixedColliderField(bounded: false, field: bounded.Field!.Field, gradientStep: Q(value: 0.01d)) });

        Assert.NotNull(@object: bounded.WorldHalfExtent());
        Assert.Null(@object: unbounded.WorldHalfExtent());
        Assert.Throws<InvalidOperationException>(testCode: () => bounded.SupportPoint(direction: FixedVector3.UnitX));
        Assert.Throws<InvalidOperationException>(testCode: () => (bounded with { Field = null }).TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity,
            position: FixedVector3.Zero, push: out _, skin: Skin, volume: Capsule()));
        Assert.Throws<ArgumentNullException>(testCode: () => FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: null!, halfExtent: FixedVector3.Zero, rotation: FixedQuaternion.Identity));
        Assert.Throws<ArgumentNullException>(testCode: () => new FixedColliderField(field: null!, gradientStep: FixedQ4816.One));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new FixedColliderField(field: bounded.Field.Field, gradientStep: FixedQ4816.Zero));
        Assert.Empty(collection: bounded.Field.Bases);
    }
}
