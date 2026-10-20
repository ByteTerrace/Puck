using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>player.signal</c> refuses a source id the input vocabulary does not know, by name, before it touches
/// any seat, so a misspelled control (<c>gamepad.south</c> for <c>gamepad.buttonSouth</c>) never echoes as delivered.
/// </summary>
public sealed class PlayerSignalSourceLawTests {
    [Fact]
    public void AnUnknownSourceIsRefusedByName() {
        // The refusal precedes every dependency the verb would use, so none is supplied.
        var registry = new CommandRegistry(modules: [new WorldBindingCommandModule(
            clock: null!,
            link: null!,
            ownedWorlds: null!,
            registry: null!,
            roster: null!,
            router: null!,
            seatBindings: null!,
            server: null!,
            windowObservers: null!
        )]);
        var refused = registry.Submit(line: "player.signal gamepad.south press");

        Assert.True(condition: refused.IsError, userMessage: refused.Output);
        Assert.Contains(expectedSubstring: "unknown source 'gamepad.south'", actualString: refused.Output);
    }
}
