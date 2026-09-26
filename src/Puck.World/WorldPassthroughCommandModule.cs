using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The local user's passthrough verb, <c>source.passthrough</c>: opens a pane's window capture as a passthrough source,
/// closes one, and echoes the opened sources and where the keyboard is. It runs only from the host's own console as typed
/// text, never from a binding, a seat, a peer, an addon or a schedule, so a world document can never open a passthrough
/// source or send one input.
/// </summary>
internal sealed class WorldPassthroughCommandModule(WorldSourcePassthrough passthrough) : ICommandModule {
    private const string Verb = "source.passthrough";

    private CommandResult Handler(CommandContext context, WireArgs args) {
        if (
            (context.Principal != Principal.Console) ||
            (context.Origin != CommandOrigin.Text)
        ) {
            return CommandResult.Error(output: $"[{Verb}: only the local user's own console may open a passthrough source; {context.Principal.Describe()} cannot]");
        }
        if (args.Count == 0) {
            return new CommandResult(Output: $"[{Verb}: {passthrough.Describe()}]");
        }
        if (
            args.Is(
                index: 0,
                value: "close"
            ) &&
            (args.Count == 2)
        ) {
            var instance = args[1].ToString();

            return (passthrough.Close(instance: instance)
                ? new CommandResult(Output: $"[{Verb}: '{instance}' closed]")
                : CommandResult.Error(output: $"[{Verb}: '{instance}' is not open]")
            );
        }
        if (
            args.Is(
                index: 0,
                value: "open"
            ) &&
            (args.Count >= 3)
        ) {
            // The window title is every token after the instance joined with spaces, since a title may contain spaces.
            var title = new StringBuilder();

            for (var token = 2; (token < args.Count); token++) {
                if (token > 2) {
                    _ = title.Append(value: ' ');
                }

                _ = title.Append(value: args[token].ToString());
            }

            return (passthrough.TryOpen(
                instance: args[1].ToString(),
                message: out var message,
                windowTitle: title.ToString()
            )
                ? new CommandResult(Output: $"[{Verb}: {message}]")
                : CommandResult.Error(output: $"[{Verb}: {message}]")
            );
        }

        return CommandResult.Error(output: $"[{Verb}: expected no argument, open <instance> <windowTitle...> or close <instance>]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: "The local user's host passthrough: source.passthrough open <instance> <windowTitle...> opens the window capture a shown views.graphs pane draws as a passthrough source, once the captured window's title contains <windowTitle> (compared without regard to case), so the local user names the window that will take their input; its pane's mapping then takes the Passthrough destination (world.view.panes). A click on the pane gives the window the keyboard, pointer events over the pane reach it at the pane's mapped client point, keys and text go to it instead of the game, each release goes where its press went, and Control+Alt+Escape returns the keyboard to the game. source.passthrough close <instance> closes one; with no argument it echoes where the keyboard is and each opened source's window, captured frame, client area and DPI scale. Only the host's own console may run it, as typed text: no binding, seat, peer, addon, schedule or world document can open a passthrough source or send it input. A source whose pane stops showing the window it was opened on is closed. Refused as unknown by a boot with no window.",
            handler: Handler,
            name: Verb
        );
    }
}
