using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the windowed World's cursor policy: any local seat's camera drag locks the pointer, and the system
/// arrow shows exactly while a passthrough source's window holds the pointer.</summary>
public sealed class WorldCursorPolicyLawTests {
    [Fact]
    public void AnySeatsCameraDragLocksThePointerAndAWindowsPointerShowsTheArrow() {
        var steeringSlot = -1;
        var windowHolds = false;
        var policy = new WorldCursorPolicy(
            steering: slot => (slot == steeringSlot),
            windowHoldsPointer: () => windowHolds
        );

        Assert.False(condition: policy.LocksPointer);
        Assert.False(condition: policy.ShowsSystemCursor);

        steeringSlot = (PlayerRoster.MaxSlots - 1);
        windowHolds = true;
        Assert.True(condition: policy.LocksPointer);
        Assert.True(condition: policy.ShowsSystemCursor);
    }
}
