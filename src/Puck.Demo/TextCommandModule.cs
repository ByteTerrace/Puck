using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Text;

namespace Puck.Demo;

/// <summary>
/// The <c>text.*</c> console verb family — the control plane for text enrichment on the diegetic terminal's CRT.
/// <c>text.motion on|off</c> is the accessibility gate that globally enables or disables every motion-class effect
/// (the WCAG reduced-motion switch on <see cref="TextMotionState"/>); <c>text.say &lt;markup&gt;</c> writes a
/// BBCode-enriched line straight to the developer console so it renders, enriched, on the terminal feed
/// (<see cref="ConsoleFeed"/>). Presentation-only: nothing here touches simulation state or the run hash, exactly like
/// every other console echo in the greenfield demo. Usage-string-on-bad-input, never throws.
/// </summary>
internal sealed class TextCommandModule(DemoConsole console) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Toggles the reduced-motion switch for text enrichment: text.motion on|off. Off freezes every motion-class effect (shake/wave/pulse/jitter/dissolve) to rest and completes reveals instantly, while colour/weight stay. Default on.",
            handler: (_, args) => {
                var enabled = ParseOnOff(args: args, current: TextMotionState.MotionEnabled);

                TextMotionState.SetMotionEnabled(enabled: enabled);

                // The echo is itself an enriched line (compiled to the control-char stream the CRT parses), so the
                // terminal shows the switch state live: the wave rolls (or holds) while the colour stays either way.
                // Routed through the console (never CommandResult): the stdin pump echoes a result RAW to stdout,
                // which would leak the control-char stream to a plain-text pipe — the console sink keeps the stream
                // for the CRT and strips the stdout copy to visible text.
                console.WriteLine(message: BbCodeTextMarkup.Compile(markup: $"[text.motion {(enabled ? "on" : "off")}] [wave]motion[/wave] - [color=#8fe0b0]colour stays[/color]"));

                return CommandResult.None;
            },
            name: "text.motion"
        );
        yield return WithArgs(
            description: "Prints a BBCode-enriched line to the terminal: text.say <markup>. Example: text.say boot [color=#ff6688]PUCK[/color] [wave]online[/wave]. Tags: color/weight/reveal (static delight) and shake/wave/pulse/jitter/dissolve (motion, gated by text.motion).",
            handler: (_, args) => {
                var markup = string.Join(separator: ' ', values: args);

                if (markup.Length == 0) {
                    return new CommandResult("[text.say: nothing to say - pass some markup, e.g. text.say hi [wave]there[/wave]]");
                }

                // Write the COMPILED control-char stream to the console so the terminal renders it enriched; ordinary
                // console lines (which carry no control chars) stay literal, so nothing else changes.
                console.WriteLine(message: BbCodeTextMarkup.Compile(markup: markup));

                return CommandResult.None;
            },
            name: "text.say"
        );
    }

    // Reads on/off (also true/false, 1/0, toggle) from the first token; an absent or unknown token toggles the current
    // state so a bare `text.motion` still flips it.
    private static bool ParseOnOff(string[] args, bool current) =>
        ((args.Length > 0) ? args[0].Trim().ToLowerInvariant() : "toggle") switch {
            "on" or "true" or "1" or "yes" or "full" => true,
            "off" or "false" or "0" or "no" or "none" => false,
            _ => !current
        };
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
