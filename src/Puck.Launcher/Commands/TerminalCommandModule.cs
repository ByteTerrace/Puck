using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher.Commands;

/// <summary>The terminal's own (engine-agnostic) command surface: just <c>quit</c>, which drives the
/// terminal through the baton (<see cref="ITerminalControl"/>). Engine-specific verbs are contributed by
/// the developer's own <see cref="ICommandModule"/>s; the registry composes them all.</summary>
internal sealed class TerminalCommandModule(
    ITerminalControl terminal
) : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            aliases: ["exit"],
            description: "Exits the terminal. Bound to Escape.",
            handler: _ => {
                terminal.RequestExit();
                return new CommandResult("Exiting…");
            },
            name: "quit",
            valueKind: CommandValueKind.Digital
        );
    }
}
