using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a seat's view of the screen it engages is the framed picture or the other view (the held view of a carried
/// screen, else the room). An engagement starts on the picture, or on the held view when a body carries the screen with
/// one; the toggle switches to the other and lasts until the engaged screen changes; a seat engaging nothing frames
/// nothing and refuses the toggle.
/// </summary>
public sealed class WorldPerceptionAnchorFrameLawTests {
    [Fact]
    public void AnEngagementStartsOnThePictureAndTheToggleSwitchesToTheRoomUntilTheSeatLeaves() {
        var anchor = new WorldPerceptionAnchor();

        Assert.False(condition: anchor.ScreenFramed(slot: 0));
        Assert.False(condition: anchor.ToggleFrame(slot: 0));

        anchor.PublishEngagedScreen(screenIndex: 2, slot: 0);
        Assert.True(condition: anchor.ScreenFramed(slot: 0));
        Assert.True(condition: anchor.ToggleFrame(slot: 0));
        Assert.False(condition: anchor.ScreenFramed(slot: 0));
        Assert.False(condition: anchor.HoldView(slot: 0));

        // The same screen published again keeps the choice; a seat engaging another or none starts over.
        anchor.PublishEngagedScreen(screenIndex: 2, slot: 0);
        Assert.False(condition: anchor.ScreenFramed(slot: 0));
        anchor.PublishEngagedScreen(screenIndex: -1, slot: 0);
        anchor.PublishEngagedScreen(screenIndex: 2, slot: 0);
        Assert.True(condition: anchor.ScreenFramed(slot: 0));
    }
    [Fact]
    public void ACarriedScreenWithAHeldViewStartsOnItAndTheToggleFramesThePicture() {
        var anchor = new WorldPerceptionAnchor();

        anchor.PublishEngagedScreen(screenIndex: 0, slot: 1);
        anchor.PublishHoldView(holdView: true, slot: 1);
        Assert.True(condition: anchor.HoldView(slot: 1));
        Assert.False(condition: anchor.ScreenFramed(slot: 1));
        Assert.True(condition: anchor.ToggleFrame(slot: 1));
        Assert.True(condition: anchor.ScreenFramed(slot: 1));

        // Put down, the screen has no held view and the same choice reads the other way.
        anchor.PublishHoldView(holdView: false, slot: 1);
        Assert.False(condition: anchor.ScreenFramed(slot: 1));
        Assert.False(condition: anchor.ScreenFramed(slot: 0));
    }
    [Fact]
    public void ASlotOutOfRangeFramesNothing() {
        var anchor = new WorldPerceptionAnchor();

        anchor.PublishEngagedScreen(screenIndex: 0, slot: WorldSeatBindings.SeatCount);
        anchor.PublishHoldView(holdView: true, slot: WorldSeatBindings.SeatCount);
        Assert.False(condition: anchor.ScreenFramed(slot: WorldSeatBindings.SeatCount));
        Assert.False(condition: anchor.HoldView(slot: -1));
        Assert.False(condition: anchor.ToggleFrame(slot: WorldSeatBindings.SeatCount));
    }
}
