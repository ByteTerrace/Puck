using Puck.Commands;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary><c>view.reseed</c> restarts the named body (body 0 by default) and refuses anything but one whole,
/// non-negative body index. <c>CreationLocomotionLawTests</c> holds what the restart does to a walking gait.</summary>
public sealed class WorldReseedCommandModuleLawTests {
    [Fact]
    public void TheVerbNamesTheBodyAndRefusesAnythingElse() {
        var registry = new CommandRegistry(modules: [new WorldReseedCommandModule(stamps: new WorldStampPool())]);

        Assert.Equal(expected: "[view.reseed: body 0 restarted 0 registration(s)]", actual: registry.Submit(line: "view.reseed").Output);
        Assert.Equal(expected: "[view.reseed: body 2 restarted 0 registration(s)]", actual: registry.Submit(line: "view.reseed 2").Output);
        Assert.True(condition: registry.Submit(line: "view.reseed -1").IsError);
        Assert.True(condition: registry.Submit(line: "view.reseed one").IsError);
        Assert.True(condition: registry.Submit(line: "view.reseed 1 2").IsError);
    }
}
