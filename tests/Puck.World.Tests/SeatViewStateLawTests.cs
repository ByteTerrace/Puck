using System.Numerics;
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
}
