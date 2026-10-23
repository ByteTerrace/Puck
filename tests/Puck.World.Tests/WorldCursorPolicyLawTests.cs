using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the windowed World's cursor policy: any local seat's camera drag locks the pointer, the system
/// arrow shows exactly while a passthrough source's window holds the pointer, and the cursor moves exactly when a pad
/// hands the policy a point.</summary>
public sealed class WorldCursorPolicyLawTests {
    [Fact]
    public void AnySeatsCameraDragLocksThePointerAndAWindowsPointerShowsTheArrow() {
        var steeringSlot = -1;
        var windowHolds = false;
        (int X, int Y)? warp = null;
        var policy = new WorldCursorPolicy(
            steering: slot => (slot == steeringSlot),
            warp: () => warp,
            windowHoldsPointer: () => windowHolds
        );

        Assert.False(condition: policy.LocksPointer);
        Assert.False(condition: policy.ShowsSystemCursor);
        Assert.False(condition: policy.TryTakeWarp(x: out _, y: out _));

        steeringSlot = (PlayerRoster.MaxSlots - 1);
        windowHolds = true;
        warp = (0, 7);
        Assert.True(condition: policy.LocksPointer);
        Assert.True(condition: policy.ShowsSystemCursor);
        Assert.True(condition: policy.TryTakeWarp(x: out var x, y: out var y));
        Assert.Equal(expected: (0, 7), actual: (x, y));
    }
}
