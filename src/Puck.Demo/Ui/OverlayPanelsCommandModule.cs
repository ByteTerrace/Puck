using System.CommandLine;
using Puck.Commands;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo.Ui;

/// <summary>
/// The overlay panels' scriptable control plane — the console-verb mirror of the master toggle and the title-band
/// drag (the unification contract: everything the mouse can do, a piped verb can do too):
/// <list type="bullet">
/// <item><c>ui.panels [on|off]</c> — master visibility for <see cref="OverlayPanelsNode"/>'s panels (toggles with no
/// argument). The console and binding bar are separate surfaces and keep their own controls.</item>
/// <item><c>ui.panel.move &lt;name&gt; &lt;x&gt; &lt;y&gt;</c> — position a draggable panel (hub | tracker | plaque),
/// exactly what a title-band drag writes; clamped on screen by the node.</item>
/// <item><c>ui.panel.reset &lt;name&gt;</c> — clear a panel's override back to its anchored default.</item>
/// </list>
/// Registered in <c>DemoHost</c> beside the other modules; a tiny module of its own (the sibling <c>ui.diegetic</c>
/// verb lives in <c>OverworldControlCommandModule</c>, but these verbs need the control STORE — adding that type
/// there would grow a module already carrying the whole control-host surface, so the ui.panels concept keeps its own
/// coupling budget here). Presentation only.
/// </summary>
internal sealed class OverlayPanelsCommandModule(OverlayPanelsControlStore store) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Shows or hides the overlay panels (toast/hub/tracker/plaque): ui.panels [on|off] (toggles with no argument).",
            handler: (_, args) => {
                var next = ((args.Length == 0) ? !store.PanelsVisible : string.Equals(a: args[0], b: "on", comparisonType: StringComparison.OrdinalIgnoreCase));

                if ((args.Length > 0) && !string.Equals(a: args[0], b: "on", comparisonType: StringComparison.OrdinalIgnoreCase) && !string.Equals(a: args[0], b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return new CommandResult("[ui.panels: usage — ui.panels [on|off]]");
                }

                store.PanelsVisible = next;

                return new CommandResult($"[ui.panels {(next ? "on" : "off")}]");
            },
            name: "ui.panels"
        );
        yield return WithArgs(
            description: "Moves a draggable overlay panel to a pixel position: ui.panel.move <hub|tracker|plaque> <x> <y> (the same override a title-band drag writes; clamped on screen).",
            handler: (_, args) => {
                if ((args.Length < 3) || !IsKnownPanel(name: args[0]) || !TryParseFloat(text: args[1], value: out var x) || !TryParseFloat(text: args[2], value: out var y)) {
                    return new CommandResult($"[ui.panel.move: usage — ui.panel.move <{PanelNamesUsage}> <x> <y>]");
                }

                store.SetOverride(panelName: args[0], position: new System.Numerics.Vector2(x: x, y: y));

                return new CommandResult($"[ui.panel.move {args[0].ToLowerInvariant()} {x:F0} {y:F0}]");
            },
            name: "ui.panel.move"
        );
        yield return WithArgs(
            description: "Resets a draggable overlay panel to its anchored default position: ui.panel.reset <hub|tracker|plaque>.",
            handler: (_, args) => {
                if ((args.Length < 1) || !IsKnownPanel(name: args[0])) {
                    return new CommandResult($"[ui.panel.reset: usage — ui.panel.reset <{PanelNamesUsage}>]");
                }

                store.ClearOverride(panelName: args[0]);

                return new CommandResult($"[ui.panel.reset {args[0].ToLowerInvariant()}]");
            },
            name: "ui.panel.reset"
        );
    }

    private static string PanelNamesUsage => string.Join(separator: "|", values: OverlayPanelsNode.DraggablePanelNames);

    private static bool IsKnownPanel(string name) {
        foreach (var known in OverlayPanelsNode.DraggablePanelNames) {
            if (string.Equals(a: known, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    // An argument-taking console verb: one trailing token list, parsed by the handler (uniform + forgiving — usage
    // strings beat parser errors on a game console). Mirrors OverworldControlCommandModule.WithArgs.
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
