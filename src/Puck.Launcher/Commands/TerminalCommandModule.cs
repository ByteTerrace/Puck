using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher.Commands;

/// <summary>The terminal's own engine-agnostic command surface: <c>quit</c> drives the baton and <c>console</c>
/// controls the invoking seat's terminal tape. Engine-specific verbs are contributed by the developer's own
/// <see cref="ICommandModule"/>s; the registry composes them all.</summary>
internal sealed class TerminalCommandModule(
    ITerminalControl terminal,
    IConsoleSessions? consoles = null
) : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            description: "Shows or hides a terminal console. A seat uses console [on|off]; the administrative console uses console [on|off] <player>.",
            handler: (context, args) => {
                if (consoles is null) {
                    return CommandResult.Error(output: "[console: no local console sessions are available]");
                }

                if (consoles.Count == 0) {
                    return CommandResult.Error(output: "[console: no local console sessions are available]");
                }

                int slot;
                var stateArguments = 0;

                if (context.Principal.Kind == CommandPrincipalKind.Seat) {
                    if (args.Count > 1) {
                        return CommandResult.Error(output: "[console: a seated invocation expects on|off, or no argument to toggle]");
                    }

                    slot = context.Slot;
                    stateArguments = args.Count;
                } else if (context.Principal.Kind == CommandPrincipalKind.Console) {
                    if (
                        (args.Count < 1) ||
                        (args.Count > 2)
                    ) {
                        return CommandResult.Error(output: "[console: administrative use requires an explicit player: console [on|off] <player>]");
                    }

                    if (
                        (args.Count == 1) &&
                        (args.Is(
                        index: 0,
                        value: "on"
                    ) || args.Is(
                        index: 0,
                        value: "off"
                    ))
                    ) {
                        return CommandResult.Error(output: "[console: administrative use requires an explicit player: console [on|off] <player>]");
                    }

                    var playerArgument = (args.Count - 1);

                    if (
                        !args.TryInt(
                        index: playerArgument,
                        value: out var player
                    ) ||
                        (player < 1) ||
                        (player > consoles.Count)
                    ) {
                        return CommandResult.Error(output: $"[console: player must be 1..{consoles.Count}]");
                    }

                    slot = (player - 1);
                    stateArguments = playerArgument;
                } else {
                    return CommandResult.Error(output: $"[console: {context.Principal.Describe()} cannot control a local console session]");
                }

                bool? requested;

                if (stateArguments == 0) {
                    requested = null;
                } else if (args.Is(
                    index: 0,
                    value: "on"
                )) {
                    requested = true;
                } else if (args.Is(
                    index: 0,
                    value: "off"
                )) {
                    requested = false;
                } else {
                    return CommandResult.Error(output: $"[console: unknown state '{args[0].ToString()}' — on|off, or no argument to toggle]");
                }

                if (!consoles.TrySetVisible(
                    resolved: out var resolved,
                    slot: slot,
                    visible: requested
                )) {
                    return CommandResult.Error(output: $"[console: seat {(slot + 1)} has no local console session]");
                }

                return new CommandResult(Output: $"[console: seat={(slot + 1)} {(resolved
                    ? "on"
                    : "off")}]");
            },
            inputScope: CommandInputScope.FocusExempt,
            name: TerminalCommandNames.Console
        );
        yield return CommandDefinition.Verb(
            aliases: ["exit"],
            // Bindable: leaving is UI navigation, not authority. No engine-default page names it today.
            bindability: CommandBindability.Bindable,
            description: "Exits the terminal.",
            handler: _ => {
                terminal.RequestExit();
                return new CommandResult("Exiting…");
            },
            name: "quit",
            valueKind: CommandValueKind.Digital
        );
    }
}
