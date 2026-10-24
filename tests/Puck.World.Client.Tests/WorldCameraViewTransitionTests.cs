using System.Numerics;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldCameraViewTransitionTests {
    private static readonly SdfCameraFirstPerson Destination = new(new Vector3(0f, 1.75f, 0.22f), false, 0.5f, 0.45f);
    private static readonly WorldCameraAutoFirstPerson Automatic = new();

    [Fact]
    public void ManualMoveIsContinuousReversibleAndKeepsTheSightline() {
        var view = new WorldCameraViewTransition();
        var head = Destination.EyePoint;
        var forward = Vector3.Normalize(new Vector3(-0.3f, -0.2f, -1f));
        var third = (head - (forward * 4.2f));
        view.SetView(true);
        var previous = 4.2f;
        for (var frame = 0; frame < 30; frame++) {
            view.Advance(Destination, Automatic, false, 4.2f, 1f / 60f);
            var eye = third;
            var target = (eye + forward);
            var opacity = 1f;
            view.Apply(head, forward, Destination.HideRadius, ref eye, ref target, ref opacity);
            var distance = Vector3.Distance(eye, head);
            Assert.InRange(distance, 0f, previous);
            Assert.InRange(Vector3.Distance(Vector3.Normalize(target - eye), forward), 0f, 0.00001f);
            if (distance <= Destination.HideRadius) {
                Assert.Equal(0f, opacity);
            }
            previous = distance;
        }
        Assert.InRange(previous, 0f, 0.00001f);
        view.SetView(false);
        view.Advance(Destination, Automatic, false, 4.2f, 0.25f);
        Assert.Equal(0.5f, view.Blend, tolerance: 0.00001f);
        view.SetView(true);
        view.Advance(Destination, Automatic, false, 4.2f, 0f);
        Assert.Equal(0.5f, view.Blend, tolerance: 0.00001f);
        view.SetView(false);
        view.Advance(Destination, Automatic, false, 4.2f, 0.25f);
        Assert.Equal(0f, view.Blend);
    }

    [Fact]
    public void AutomaticRequiresConsecutiveDwellAndSeparateExitClearance() {
        var view = new WorldCameraViewTransition();
        view.Advance(Destination, Automatic, false, 0.1f, 1f);
        Assert.False(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 0.8f, 0.19f);
        Assert.False(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 1f, 0.1f);
        view.Advance(Destination, Automatic, true, 0.8f, 0.19f);
        Assert.False(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 0.8f, 0.02f);
        Assert.True(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 1.1f, 2f);
        Assert.True(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 1.4f, 0.14f);
        Assert.True(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 1.1f, 0.1f);
        view.Advance(Destination, Automatic, true, 1.4f, 0.14f);
        Assert.True(view.FirstPerson);
        view.Advance(Destination, Automatic, true, 1.4f, 0.02f);
        Assert.False(view.FirstPerson);
    }

    [Fact]
    public void ManualChoiceOverridesStateAndAutomaticUntilRestored() {
        var view = new WorldCameraViewTransition();
        view.SetView(false);
        view.Advance(Destination with { Active = true }, Automatic, true, 0.1f, 1f);
        Assert.False(view.FirstPerson);
        view.SetView(null);
        view.Advance(Destination with { Active = true }, Automatic, true, 4.2f, 1f);
        Assert.True(view.FirstPerson);
        view.Advance(Destination, Automatic, false, 0.1f, 1f);
        Assert.False(view.FirstPerson);
        view.Reset();
        Assert.Equal(0f, view.Blend);
    }

    [Fact]
    public void AutomaticPreferenceIsSeatLocalAndPortablePreferenceBeatsAuthor() {
        var seat = new WorldSeatViewState();
        var other = new WorldSeatViewState();
        var control = new WorldSeatViewControl(WorldSeatYawReference.World, -0.5f, 1.2f);
        var feel = WorldSeatCameraFeel.Default;
        Assert.False(seat.AutomaticFirstPerson(control, feel));
        control = control with { AutoFirstPerson = new WorldCameraAutoFirstPerson(Enabled: true) };
        Assert.True(seat.AutomaticFirstPerson(control, feel));
        feel = feel with { AutoFirstPerson = false };
        Assert.False(seat.AutomaticFirstPerson(control, feel));
        seat.SetAutomaticFirstPerson(true);
        Assert.True(seat.AutomaticFirstPerson(control, feel));
        Assert.False(other.AutomaticFirstPerson(control, feel));
        seat.SetAutomaticFirstPerson(null);
        Assert.False(seat.AutomaticFirstPerson(control, feel));
    }

    [InlineData(1f)]
    [InlineData(0.25f)]
    [Theory]
    public void SeatArrivesAtTheAuthoredEyeInTheLiveBodyFrame(float scale) {
        var seat = new WorldSeatViewState();
        var other = new WorldSeatViewState();
        var root = new Vector3(12f, 2f, 38f);
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var subject = new WorldChaseBody(null, root, orientation, scale);
        var head = (root + Vector3.Transform(Destination.EyePoint * scale, orientation));
        var control = new WorldSeatViewControl(WorldSeatYawReference.World, -0.5f, 1.2f);
        seat.SetView(true);
        var target = (root + (Vector3.UnitY * scale));
        var eye = (target + (Vector3.UnitZ * 4.2f * scale));
        var near = seat.Clear(true, null, ref eye, ref target, subject, Vector3.UnitY,
            SdfCameraDynamics.None, 0.5f, control: control, scale: scale, firstPerson: Destination);
        Assert.InRange(Vector3.Distance(head, eye), 0f, 0.00001f);
        Assert.Equal(-Vector3.UnitZ, Vector3.Normalize(target - eye));
        Assert.Equal(0f, near);
        Assert.Equal(0f, seat.BodyOpacity);
        Assert.Equal(1f, other.BodyOpacity);
        Assert.Contains("view=first", seat.DescribeClearance());
        Assert.Contains("transition=1", seat.DescribeClearance());
    }
}
