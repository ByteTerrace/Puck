using System.Numerics;

using Xunit;

using Puck.Maths;
using Puck.SdfVm.Views;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Binds the float twin (<see cref="SecondOrderResponse"/>/<see cref="SecondOrderFollower3"/>) against the fixed
/// authority (<see cref="SecondOrderDynamics"/>) it must never diverge far from, and pins the qualitative shapes a
/// second-order follower is authored for (no overshoot at critical damping, dt-independence, large-step stability,
/// exponential equivalence at the migration's chosen parameters).
/// </summary>
public sealed class SecondOrderFollowerTests {
    private const float StepSeconds = (1f / 240f);
    private const ulong TicksPerSecond = 50400UL;

    public static IEnumerable<object[]> SteppedVsClosedFormCases() {
        foreach (var zeta in new[] { 0.5f, 1f, 2f }) {
            foreach (var (e0, v0) in new[] { (1f, 0f), (0f, 3f) }) {
                yield return [zeta, e0, v0];
            }
        }
    }
    [MemberData(nameof(SteppedVsClosedFormCases))]
    [Theory]
    public void StepAgreesWithFixedEvaluateOverAWalk(float zeta, float initialError, float initialVelocity) {
        const float frequencyHz = 2f;
        var response = SecondOrderResponse.Create(dampingRatio: zeta, frequencyHz: frequencyHz, initialResponse: 0f);
        var fixedDynamics = SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromDouble(value: frequencyHz),
            dampingRatio: FixedQ4816.FromDouble(value: zeta),
            initialResponse: FixedQ4816.Zero
        );

        var target = 0f;
        var position = (target + initialError);
        var velocity = initialVelocity;
        var propagator = response.Propagator(deltaSeconds: StepSeconds);
        var checkpoints = new HashSet<int> { 1, 60, 240, 480 };

        for (var step = 1; (step <= 480); ++step) {
            SecondOrderResponse.Step(
                position: ref position,
                velocity: ref velocity,
                target: target,
                targetVelocity: 0f,
                propagator: propagator,
                targetVelocityGain: response.TargetVelocityGain
            );

            if (!checkpoints.Contains(item: step)) {
                continue;
            }

            var elapsedTicks = (((ulong)step) * 210UL); // 210 ticks at 50400 tps == one 1/240 s frame.
            var fixedSample = fixedDynamics.Evaluate(
                initialValue: FixedQ4816.FromDouble(value: (target + initialError)),
                initialVelocity: FixedQ4816.FromDouble(value: initialVelocity),
                target: FixedQ4816.FromDouble(value: target),
                elapsedTicks: elapsedTicks,
                ticksPerSecond: TicksPerSecond
            );

            Assert.True(
                condition: (MathF.Abs(x: (position - (((float)fixedSample.Value.Value) / 65536f))) < 2e-3f),
                userMessage: $"step {step}: float={position} fixed={(((float)fixedSample.Value.Value) / 65536f)}"
            );
        }
    }
    [Fact]
    public void CriticalDampingFromRestNeverOvershoots() {
        var response = SecondOrderResponse.Create(dampingRatio: 1f, frequencyHz: 2f, initialResponse: 0f);
        var follower = new SecondOrderFollower3();
        var target = new Vector3(x: 10f, y: 0f, z: 0f);
        var maxX = float.MinValue;

        follower.Seed(target: Vector3.Zero);

        for (var i = 0; (i < (240 * 10)); ++i) {
            var value = follower.Step(deltaSeconds: StepSeconds, response: response, target: target);

            maxX = MathF.Max(x: maxX, y: value.X);
        }

        Assert.True(condition: (maxX <= (10f + 1e-4f)));
        Assert.True(condition: (MathF.Abs(x: (maxX - 10f)) < 1e-3f));
    }
    [Fact]
    public void LightDampingFromRestOvershoots() {
        var response = SecondOrderResponse.Create(dampingRatio: 0.2f, frequencyHz: 2f, initialResponse: 0f);
        var follower = new SecondOrderFollower3();
        var target = new Vector3(x: 10f, y: 0f, z: 0f);
        var maxX = float.MinValue;

        follower.Seed(target: Vector3.Zero);

        for (var i = 0; (i < (240 * 10)); ++i) {
            var value = follower.Step(deltaSeconds: StepSeconds, response: response, target: target);

            maxX = MathF.Max(x: maxX, y: value.X);
        }

        Assert.True(condition: (maxX > 10.05f));
    }
    [Fact]
    public void OneLargeStepAgreesWithTenSmallStepsWithinATolerance() {
        var response = SecondOrderResponse.Create(dampingRatio: 0.7f, frequencyHz: 1f, initialResponse: 0f);
        var target = new Vector3(x: 5f, y: -2f, z: 1f);

        var coarse = new SecondOrderFollower3();

        coarse.Seed(target: Vector3.Zero);

        var coarseValue = coarse.Step(deltaSeconds: 0.1f, response: response, target: target);

        var fine = new SecondOrderFollower3();

        fine.Seed(target: Vector3.Zero);

        var fineValue = Vector3.Zero;

        for (var i = 0; (i < 10); ++i) {
            fineValue = fine.Step(deltaSeconds: 0.01f, response: response, target: target);
        }

        Assert.True(condition: ((coarseValue - fineValue).Length() < 1e-3f));
    }
    [Fact]
    public void ALargeStepLandsNearTheTargetWithoutOvershootAtCriticalDamping() {
        var response = SecondOrderResponse.Create(dampingRatio: 1f, frequencyHz: 1f, initialResponse: 0f);
        var follower = new SecondOrderFollower3();

        follower.Seed(target: Vector3.Zero);

        var value = follower.Step(response: response, deltaSeconds: 10f, target: new Vector3(x: 20f, y: 0f, z: 0f));

        Assert.True(condition: (MathF.Abs(x: (value.X - 20f)) < 1e-3f));
        Assert.True(condition: (value.X <= (20f + 1e-4f)));
    }
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [Theory]
    public void CriticalDampingMatchesTheOldExponentialCameraSmootherAtTheMigratedFrequency(float rate) {
        // The camera migration's falsifier: ζ = 1, r = 1, f = rate/(2π) makes the second-order system identical, in
        // continuous time, to the retired exponential smoother y(t) = target − (target − y0)·e^(−rate·t).
        var frequencyHz = (rate / (2f * MathF.PI));
        var response = SecondOrderResponse.Create(dampingRatio: 1f, frequencyHz: frequencyHz, initialResponse: 1f);
        var follower = new SecondOrderFollower3();
        var start = new Vector3(x: 10f, y: 0f, z: 0f);
        var target = new Vector3(x: 30f, y: 0f, z: 0f);

        follower.Seed(target: start);

        var elapsed = 0f;

        foreach (var checkpoint in new[] { 0.25f, 0.5f, 1f, 2f }) {
            while (elapsed < (checkpoint - 1e-6f)) {
                follower.Step(deltaSeconds: StepSeconds, response: response, target: target);
                elapsed += StepSeconds;
            }

            var expected = (30f - (20f * MathF.Exp(x: (-rate * checkpoint))));

            Assert.True(
                condition: (MathF.Abs(x: (follower.Value.X - expected)) < (0.05f * 20f)),
                userMessage: $"rate={rate} t={checkpoint}: follower={follower.Value.X} expected={expected}"
            );
        }
    }
    [Fact]
    public void ReseedDropsTheLagAndTheNextStepSnapsToTheTarget() {
        var response = SecondOrderResponse.Create(dampingRatio: 1f, frequencyHz: 2f, initialResponse: 0f);
        var follower = new SecondOrderFollower3();

        follower.Seed(target: Vector3.Zero);
        follower.Step(response: response, deltaSeconds: StepSeconds, target: new Vector3(x: 10f, y: 0f, z: 0f));

        Assert.NotEqual(Vector3.Zero, follower.Value);

        follower.Reseed();

        var target = new Vector3(x: 4f, y: 5f, z: 6f);
        var value = follower.Step(deltaSeconds: StepSeconds, response: response, target: target);

        Assert.Equal(actual: value, expected: target);
        Assert.Equal(Vector3.Zero, follower.Velocity);
    }
    [Fact]
    public void StepPoseFirstCallSnapsToTheTargetUnseeded() {
        var response = SecondOrderResponse.Create(dampingRatio: 1f, frequencyHz: 2f, initialResponse: 0f);
        var position = new SecondOrderFollower3();
        var orientation = new SecondOrderFollower4();
        var target = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.4f);

        var (steppedPosition, steppedOrientation) = SecondOrderPoseFollower.StepPose(
            position: ref position,
            orientation: ref orientation,
            response: in response,
            deltaSeconds: StepSeconds,
            targetPosition: new Vector3(x: 3f, y: -1f, z: 2f),
            targetOrientation: target
        );

        Assert.Equal(new Vector3(x: 3f, y: -1f, z: 2f), steppedPosition);
        Assert.Equal(actual: steppedOrientation, expected: target);
    }
    [Fact]
    public void StepPoseHemisphereMatchesASignNegatedTargetAgainstItsPreviousTarget() {
        var response = SecondOrderResponse.Create(dampingRatio: 0.5f, frequencyHz: 2f, initialResponse: 0f);
        var start = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0f);
        var wanted = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.2f);
        var negatedWanted = new Quaternion(w: -wanted.W, x: -wanted.X, y: -wanted.Y, z: -wanted.Z); // same rotation, opposite sign

        // A follower fed the sign-negated (but rotationally identical) target every other step.
        var flippedPosition = new SecondOrderFollower3();
        var flippedOrientation = new SecondOrderFollower4();

        flippedPosition.Seed(target: Vector3.Zero);
        flippedOrientation.Seed(target: new Vector4(w: start.W, x: start.X, y: start.Y, z: start.Z));

        // A reference follower fed the SAME (unnegated) target every step.
        var referencePosition = new SecondOrderFollower3();
        var referenceOrientation = new SecondOrderFollower4();

        referencePosition.Seed(target: Vector3.Zero);
        referenceOrientation.Seed(target: new Vector4(w: start.W, x: start.X, y: start.Y, z: start.Z));

        Quaternion flippedResult = default;
        Quaternion referenceResult = default;

        for (var step = 0; (step < 6); ++step) {
            var thisStepTarget = (((step % 2) == 0) ? wanted : negatedWanted);

            (_, flippedResult) = SecondOrderPoseFollower.StepPose(
                position: ref flippedPosition,
                orientation: ref flippedOrientation,
                response: in response,
                deltaSeconds: StepSeconds,
                targetPosition: Vector3.Zero,
                targetOrientation: thisStepTarget
            );
            (_, referenceResult) = SecondOrderPoseFollower.StepPose(
                position: ref referencePosition,
                orientation: ref referenceOrientation,
                response: in response,
                deltaSeconds: StepSeconds,
                targetPosition: Vector3.Zero,
                targetOrientation: wanted
            );
        }

        // Hemisphere matching means the sign-negated target never made the follower ease the long way around, so
        // both followers land in the same orientation to within float rounding of the underlying arithmetic.
        Assert.True(condition: (MathF.Abs(x: (flippedResult.X - referenceResult.X)) < 1e-5f));
        Assert.True(condition: (MathF.Abs(x: (flippedResult.Y - referenceResult.Y)) < 1e-5f));
        Assert.True(condition: (MathF.Abs(x: (flippedResult.Z - referenceResult.Z)) < 1e-5f));
        Assert.True(condition: (MathF.Abs(x: (flippedResult.W - referenceResult.W)) < 1e-5f));
    }
}
