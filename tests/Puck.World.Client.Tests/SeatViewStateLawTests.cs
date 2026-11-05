using System.Numerics;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>The seat camera's live yaw is relative to the authored yaw frame: recentering names one world heading,
/// so a body-relative frame must not add that heading a second time.</summary>
public sealed class SeatViewStateLawTests {
    /// <summary>A negative closing rate is refused rather than reversing the ease: <see cref="FirstOrderLag.Alpha"/>
    /// clamps it to zero, so <see cref="WorldSeatViewState.Follow(float,float,float)"/> leaves the live yaw exactly where it was — the
    /// intended behavior change from the deleted copy's own unclamped <c>1 - exp(-rate * dt)</c>, which moved
    /// backward away from the target for a negative rate instead of holding still.</summary>
    [Fact]
    public void ExplicitRecenterCutsAnUnsmoothedCameraAndOrdinaryFramesDoNot() {
        var state = new WorldSeatViewState();
        var before = state.CutRevision;

        state.Recenter(heading: 0f, views: Fixtures.BuildDocument().Views);
        Assert.True(condition: (state.CutRevision > before));
        var cut = state.CutRevision;
        var eye = Vector3.UnitZ;
        var target = Vector3.Zero;

        for (var frame = 0; (frame < 3); frame++) {
            state.Follow(dynamics: SdfCameraDynamics.None, enabled: false, deltaSeconds: 0.1f, eye: ref eye, target: ref target);
            Assert.Equal(expected: cut, actual: state.CutRevision);
        }
    }
    [Fact]
    public void Follow_NegativeRate_HoldsTheLiveYawExactly() {
        var state = new WorldSeatViewState();

        state.Follow(
            deltaSeconds: 1f,
            rate: -1f,
            targetYaw: 1.5f
        );

        Assert.Equal(
            expected: 0f,
            actual: state.Yaw
        );
    }
    /// <summary>Control for <see cref="Follow_NegativeRate_HoldsTheLiveYawExactly"/>: the same call with a positive
    /// rate DOES move the live yaw, so the hold above is a discriminating assertion about the sign of the rate
    /// rather than <see cref="WorldSeatViewState.Follow(float,float,float)"/> being inert generally.</summary>
    [Fact]
    public void Follow_PositiveRate_MovesTheLiveYawTowardTheTarget() {
        var state = new WorldSeatViewState();

        state.Follow(
            deltaSeconds: 1f,
            rate: 5f,
            targetYaw: 1.5f
        );

        Assert.NotEqual(
            expected: 0f,
            actual: state.Yaw
        );
    }
    /// <summary>A frame between two fixed ticks presents the tick's look step at the fraction the body is drawn at,
    /// while a pointer nudge between ticks shows whole at once and a recenter cuts the step.</summary>
    [Fact]
    public void PresentedLook_InterpolatesTheTickStepAndNothingElse() {
        var views = Fixtures.BuildDocument().Views;
        var feel = WorldSeatCameraFeel.Default;
        var state = new WorldSeatViewState();

        state.BeginTick();
        // A full right look for one tick: the live yaw moves by -0.1 rad (the orbit turns opposite the look).
        state.Nudge(input: new Vector2(x: 1f, y: 0f), yawScale: 0.1f, pitchScale: 0.1f, preference: feel, views: views);
        state.CompleteTick();

        Assert.Equal(expected: 0f, actual: state.PresentedLook(interpolationAlpha: 0f).Yaw, tolerance: 1e-6f);
        Assert.Equal(expected: -0.05f, actual: state.PresentedLook(interpolationAlpha: 0.5f).Yaw, tolerance: 1e-6f);
        Assert.Equal(expected: -0.1f, actual: state.PresentedLook(interpolationAlpha: 1f).Yaw, tolerance: 1e-6f);

        // A pointer nudge outside a tick moves the presented look by its whole amount at every fraction.
        state.Nudge(input: new Vector2(x: 0.2f, y: 0f), yawScale: 1f, pitchScale: 1f, preference: feel, views: views);

        Assert.Equal(expected: -0.2f, actual: state.PresentedLook(interpolationAlpha: 0f).Yaw, tolerance: 1e-6f);
        Assert.Equal(expected: -0.3f, actual: state.PresentedLook(interpolationAlpha: 1f).Yaw, tolerance: 1e-6f);

        state.Recenter(heading: 0f, views: views);

        Assert.Equal(expected: 0f, actual: state.PresentedLook(interpolationAlpha: 0f).Yaw);
        Assert.Equal(expected: 0f, actual: state.PresentedLook(interpolationAlpha: 0f).Pitch);
    }
    /// <summary>A world-referenced live yaw turns with a body that was posed to a new heading (a pose epoch under the
    /// same authority claim) and keeps the camera's place around it; an ordinary turn, a new claim and a body-referenced
    /// yaw leave it alone, and a recenter seats the camera behind the body's heading rather than at world zero.</summary>
    [Fact]
    public void AWorldReferencedLookTurnsWithAPosedBodyAndRecentersBehindIt() {
        var views = Fixtures.BuildDocument().Views;
        var world = views with { SeatControlRaw = views.SeatControl with { YawReference = WorldSeatYawReference.World, }, };
        var body = views with { SeatControlRaw = views.SeatControl with { YawReference = WorldSeatYawReference.Body, }, };
        var state = new WorldSeatViewState();

        state.ObserveBody(routeEpoch: 1UL, poseEpoch: 1, heading: 0f, yawReference: WorldSeatYawReference.World);
        // The body turns by degrees without a new epoch: the look does not follow.
        state.ObserveBody(routeEpoch: 1UL, poseEpoch: 1, heading: 0.5f, yawReference: WorldSeatYawReference.World);
        Assert.Equal(expected: 0f, actual: state.Yaw);
        // Posed half a turn round: the look turns with it.
        state.ObserveBody(routeEpoch: 1UL, poseEpoch: 2, heading: (0.5f + MathF.PI), yawReference: WorldSeatYawReference.World);
        Assert.Equal(expected: MathF.PI, actual: Math.Abs(state.Yaw), tolerance: 1e-5f);
        // A new authority claim records the body without turning the look.
        var turned = state.Yaw;

        state.ObserveBody(routeEpoch: 2UL, poseEpoch: 9, heading: 1f, yawReference: WorldSeatYawReference.World);
        Assert.Equal(expected: turned, actual: state.Yaw);
        // A body-referenced yaw already rides the heading.
        var rides = new WorldSeatViewState();

        rides.ObserveBody(routeEpoch: 1UL, poseEpoch: 1, heading: 0f, yawReference: WorldSeatYawReference.Body);
        rides.ObserveBody(routeEpoch: 1UL, poseEpoch: 2, heading: 2f, yawReference: WorldSeatYawReference.Body);
        Assert.Equal(expected: 0f, actual: rides.Yaw);
        state.Recenter(heading: 1.25f, views: world);
        Assert.Equal(expected: 1.25f, actual: state.Yaw, tolerance: 1e-6f);
        state.Recenter(heading: 1.25f, views: body);
        Assert.Equal(expected: 0f, actual: state.Yaw);
    }
    [Fact]
    public void Recenter_BodyRelativeYaw_DoesNotDoubleBodyHeading() {
        var document = Fixtures.BuildDocument();
        var views = document.Views with {
            SeatControlRaw = document.Views.SeatControl with { YawReference = WorldSeatYawReference.Body, },
        };
        var bodyOrientation = Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: 0.73f
        );
        var state = new WorldSeatViewState();
        var bodyHeading = state.LogicalYaw(
            bodyOrientation: bodyOrientation,
            views: views
        );

        state.RecenterLook(
            targetYaw: bodyHeading,
            views: views
        );

        Assert.Equal(
            expected: bodyHeading,
            actual: state.LogicalYaw(
                bodyOrientation: bodyOrientation,
                views: views
            ),
            precision: 5
        );
    }
}
