using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The certified queries prove what they answer. A certified sweep never carries a sphere through a surface, however
/// thin the surface and however long the step, where a fixed-step stepper that samples the field only at each step's ends
/// passes straight through the same wall (the red leg, which keeps the fixture honest). Every point of a certified sweep
/// keeps the point field above the radius, and a certified line of sight's Clear and Blocked agree with the point field
/// along the segment.
/// </summary>
public sealed class SdfCertifiedQueryLawTests {
    private const float Radius = 0.1f;
    private const float WallHalfThickness = 0.005f;

    [InlineData(1f)]
    [InlineData(10f)]
    [InlineData(1000f)]
    [InlineData(100000f)]
    [Theory]
    public void ACertifiedSweepNeverTunnelsThroughAThinWall(float speed) {
        var evaluator = ThinWall();

        for (var lane = -4; (lane <= 4); lane++) {
            var startX = -0.6f;
            var start = Fixed(value: new Vector3(x: startX, y: (0.37f * lane), z: (-0.21f * lane)));
            var displacement = Fixed(value: new Vector3(x: speed, y: (0.01f * lane), z: 0f));

            Assert.True(condition: evaluator.TryCertifiedSweep(
                displacement: displacement,
                origin: FixedPosition.FromLocal(local: start),
                radius: FixedQ4816.FromDouble(value: Radius),
                sweep: out var sweep
            ));

            Assert.True(condition: sweep.Reached.TryDelta(delta: out var reachedPoint, origin: FixedPosition.Zero));

            var reached = reachedPoint.X;
            var face = FixedQ4816.FromDouble(value: (-WallHalfThickness - Radius));

            Assert.Equal(expected: SdfCertifiedSweepOutcome.Contact, actual: sweep.Outcome);
            Assert.True(condition: (reached <= face), userMessage: $"speed {speed}, lane {lane}: the sphere's centre reached {reached}, past the wall's face at {face}");
            // Progress: the sweep stops close to contact, not at its start.
            Assert.True(
                condition: ((face - reached) < FixedQ4816.FromDouble(value: 0.01)),
                userMessage: $"speed {speed}, lane {lane}: the sweep stopped {(face - reached)} short of the wall after {sweep.Steps} steps"
            );
        }
    }
    [InlineData(10f)]
    [InlineData(1000f)]
    [Theory]
    public void AFixedStepStepperTunnelsThroughTheSameWall(float speed) {
        var evaluator = ThinWall();
        var start = Fixed(value: new Vector3(x: -2.5f, y: 0f, z: 0f));
        var end = Fixed(value: new Vector3(x: (-2.5f + speed), y: 0f, z: 0f));
        var radius = FixedQ4816.FromDouble(value: Radius);

        Assert.True(condition: evaluator.TryDistance(distance: out var atStart, material: out _, position: FixedPosition.FromLocal(local: start)));
        Assert.True(condition: evaluator.TryDistance(distance: out var atEnd, material: out _, position: FixedPosition.FromLocal(local: end)));
        // Both ends are clear, so a stepper that tests only the ends accepts the move, and lands past the wall.
        Assert.True(condition: (atStart > radius));
        Assert.True(condition: (atEnd > radius));
        Assert.True(condition: (end.X > FixedQ4816.FromDouble(value: WallHalfThickness)));
    }
    [Fact]
    public void EveryPointOfACertifiedSweepStaysClear() {
        var evaluator = Scene();
        var random = new Random(Seed: 1234);
        var radius = FixedQ4816.FromDouble(value: Radius);

        for (var trial = 0; (trial < 64); trial++) {
            var start = Fixed(value: new Vector3(x: Next(random: random, scale: 4f), y: (1.5f + Next(random: random, scale: 1f)), z: Next(random: random, scale: 4f)));
            var displacement = Fixed(value: new Vector3(x: Next(random: random, scale: 6f), y: Next(random: random, scale: 3f), z: Next(random: random, scale: 6f)));

            Assert.True(condition: evaluator.TryCertifiedSweep(displacement: displacement, origin: FixedPosition.FromLocal(local: start), radius: radius, sweep: out var sweep));
            Assert.True(condition: evaluator.TryDistance(distance: out var atStart, material: out _, position: FixedPosition.FromLocal(local: start)));

            // A sphere that starts within its radius of a surface is certified nowhere.
            if (atStart <= radius) {
                Assert.Equal(expected: SdfCertifiedSweepOutcome.Contact, actual: sweep.Outcome);
                Assert.Equal(expected: FixedQ4816.Zero, actual: sweep.Fraction);
                continue;
            }

            for (var sample = 0; (sample <= 64); sample++) {
                var t = FixedQ4816.FromRawBits(value: ((sweep.Fraction.Value * sample) / 64L));
                var point = (start + (displacement * t));

                Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: point)));
                Assert.True(condition: (distance > radius), userMessage: $"trial {trial}: the certified sweep reached {sweep.Fraction}, but at {t} the field reads {distance}, not above the radius");
            }

            Assert.True(condition: evaluator.TryDistance(distance: out var atReached, material: out _, position: sweep.Reached));
            Assert.True(condition: (atReached > radius), userMessage: $"trial {trial}: the reached centre reads {atReached}, not above the radius");
        }
    }
    [Fact]
    public void ACertifiedLineOfSightAgreesWithThePointField() {
        var evaluator = Scene();
        var random = new Random(Seed: 99);
        var verdicts = new Dictionary<SdfCertifiedVisibility, int>();

        for (var trial = 0; (trial < 96); trial++) {
            var from = Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f)));
            var to = Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f)));

            Assert.True(condition: evaluator.TryCertifiedLineOfSight(from: FixedPosition.FromLocal(local: from), to: FixedPosition.FromLocal(local: to), visibility: out var visibility));
            verdicts[visibility] = (verdicts.GetValueOrDefault(key: visibility) + 1);

            var least = FixedQ4816.MaxValue;

            for (var sample = 0; (sample <= 256); sample++) {
                var point = (from + ((to - from) * FixedQ4816.FromRawBits(value: ((FixedQ4816.One.Value * sample) / 256L))));

                Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: point)));
                least = FixedQ4816.Min(x: least, y: distance);
            }

            if (visibility == SdfCertifiedVisibility.Clear) {
                Assert.True(condition: (least > FixedQ4816.Zero), userMessage: $"trial {trial}: certified clear, but a sample reads {least}");
            }
        }

        // The fixture reaches both proved verdicts.
        Assert.True(condition: (verdicts.GetValueOrDefault(key: SdfCertifiedVisibility.Clear) > 0));
        Assert.True(condition: (verdicts.GetValueOrDefault(key: SdfCertifiedVisibility.Blocked) > 0));
    }
    [Fact]
    public void ACertifiedLineOfSightSeesAThinWall() {
        var evaluator = ThinWall();

        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -3f, y: 0.2f, z: 0.1f))),
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: 3f, y: -0.1f, z: 0.3f))),
            visibility: out var through
        ));
        Assert.Equal(actual: through, expected: SdfCertifiedVisibility.Blocked);
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -3f, y: 0.2f, z: 0.1f))),
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -0.5f, y: -0.1f, z: 0.3f))),
            visibility: out var before
        ));
        Assert.Equal(actual: before, expected: SdfCertifiedVisibility.Clear);
    }

    private static SdfFieldEvaluator ThinWall() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Box(halfExtents: new Vector3(x: WallHalfThickness, y: 4f, z: 4f), material: material, round: 0f);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static SdfFieldEvaluator Scene() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder
            .Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .ResetPoint().Translate(offset: new Vector3(x: 1f, y: 1.5f, z: -1f)).Sphere(material: material, radius: 0.8f)
            .ResetPoint().Translate(offset: new Vector3(x: -2f, y: 1f, z: 1.5f)).Rotate(rotation: Quaternion.Normalize(value: new Quaternion(w: 0.9f, x: 0.1f, y: 0.4f, z: 0f))).Box(halfExtents: new Vector3(x: 0.7f, y: 1f, z: 0.3f), material: material, round: 0.05f)
            .ResetPoint().Translate(offset: new Vector3(x: 2.5f, y: 1f, z: 2f)).Torus(blend: SdfBlendOp.SmoothUnion, majorRadius: 0.9f, material: material, minorRadius: 0.25f, smooth: 0.3f);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedVector3 Fixed(Vector3 value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z)
        );
    private static float Next(Random random, float scale) =>
        (((((float)random.NextDouble()) * 2f) - 1f) * scale);
}
