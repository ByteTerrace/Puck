using System.Numerics;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The seat camera's live yaw is relative to the authored yaw frame: recentering names one world heading,
/// so a body-relative frame must not add that heading a second time.</summary>
public sealed class SeatViewStateLawTests {
    [Fact]
    public void Recenter_BodyRelativeYaw_DoesNotDoubleBodyHeading() {
        var document = Fixtures.BuildDocument();
        var views = document.Views with {
            SeatControl = document.Views.SeatControl with { YawReference = WorldSeatYawReference.Body, },
        };
        var bodyOrientation = Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: 0.73f
        );
        var state = new WorldSeatViewState();
        var bodyHeading = state.LogicalYaw(
            views: views,
            bodyOrientation: bodyOrientation
        );

        state.RecenterLook(
            targetYaw: bodyHeading,
            views: views
        );

        Assert.Equal(
            expected: bodyHeading,
            actual: state.LogicalYaw(
                views: views,
                bodyOrientation: bodyOrientation
            ),
            precision: 5
        );
    }

    /// <summary>A negative closing rate is refused rather than reversing the ease: <see cref="FirstOrderLag.Alpha"/>
    /// clamps it to zero, so <see cref="WorldSeatViewState.Follow(float,float,float)"/> leaves the live yaw exactly where it was — the
    /// intended behavior change from the deleted copy's own unclamped <c>1 - exp(-rate * dt)</c>, which moved
    /// backward away from the target for a negative rate instead of holding still.</summary>
    [Fact]
    public void Follow_NegativeRate_HoldsTheLiveYawExactly() {
        var state = new WorldSeatViewState();

        state.Follow(
            targetYaw: 1.5f,
            rate: -1f,
            deltaSeconds: 1f
        );

        Assert.Equal(expected: 0f, actual: state.Yaw);
    }

    /// <summary>Control for <see cref="Follow_NegativeRate_HoldsTheLiveYawExactly"/>: the same call with a positive
    /// rate DOES move the live yaw, so the hold above is a discriminating assertion about the sign of the rate
    /// rather than <see cref="WorldSeatViewState.Follow(float,float,float)"/> being inert generally.</summary>
    [Fact]
    public void Follow_PositiveRate_MovesTheLiveYawTowardTheTarget() {
        var state = new WorldSeatViewState();

        state.Follow(
            targetYaw: 1.5f,
            rate: 5f,
            deltaSeconds: 1f
        );

        Assert.NotEqual(expected: 0f, actual: state.Yaw);
    }
}
