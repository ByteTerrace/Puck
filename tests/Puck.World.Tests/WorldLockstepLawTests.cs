using Puck.Commands;
using Puck.Launcher;
using Xunit;

namespace Puck.World.Tests;

/// <summary><c>world.lockstep</c> reads and sets the window pump's lockstep presentation lever, and refuses any other
/// argument without moving it.</summary>
public sealed class WorldLockstepLawTests {
    [Fact]
    public void TheVerbSetsAndReadsTheLever() {
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var registry = new CommandRegistry(modules: [new WorldLockstepCommandModule(pacing: pacing)]);

        Assert.StartsWith(expectedStartString: "[world.lockstep: off", actualString: registry.Submit(line: "world.lockstep").Output, comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "[world.lockstep: on", actualString: registry.Submit(line: "world.lockstep on").Output, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: pacing.TickLocked);
        Assert.True(condition: registry.Submit(line: "world.lockstep sideways").IsError);
        Assert.True(condition: pacing.TickLocked);
        Assert.StartsWith(expectedStartString: "[world.lockstep: off", actualString: registry.Submit(line: "world.lockstep off").Output, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: pacing.TickLocked);
    }
}
