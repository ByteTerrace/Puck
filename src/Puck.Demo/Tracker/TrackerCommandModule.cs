using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Forge;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's console-assist verbs — the precise/named half of the pad-first + console-assist input model,
/// mirroring <see cref="Creator.CreatorCommandModule"/>'s shape exactly. Open the backtick console and type
/// <c>tracker.rows</c> to see the current pattern; every other verb here gives the pad's nudges exact, named
/// control (load/save a document by name, set a row's note directly, set the tempo, start/stop the preview).
/// Reaches the SAME mode-state singleton the overworld node's pad takeover drives through
/// <see cref="ForgeCommands.TrackerModeInstance"/> (an <see cref="IServiceProvider"/> lookup rather than the
/// <c>IRenderNode</c> root cast <see cref="Creator.CreatorCommandModule"/> uses, so this module works from the
/// console alone — the tracker has no other host-specific state to reach).
/// </summary>
internal sealed class TrackerCommandModule(IServiceProvider services) : ICommandModule {
    private TrackerScene Scene => ForgeCommands.TrackerModeInstance(services: services).Scene;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in GetDocumentCommands()) { yield return command; }
        foreach (var command in GetEditCommands()) { yield return command; }
        foreach (var command in GetPlaybackCommands()) { yield return command; }
    }

    // The document verbs: list/load/save/new.
    private IEnumerable<CommandDefinition> GetDocumentCommands() {
        yield return Plain(
            description: "Lists the saved tunes under ./tunes/.",
            handler: _ => new CommandResult((AudioDocumentStore.List() is { Count: > 0 } names)
                ? $"[tracker.list: {string.Join(separator: ", ", values: names)}]"
                : "[tracker.list: none saved yet — tracker.save <name> writes one]"),
            name: "tracker.list"
        );
        yield return WithArgs(
            description: "Loads a saved tune (or an explicit file path): tracker.load <name>.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (args.Length == 0) {
                    return "[tracker.load: give a name — tracker.list shows what's saved]";
                }

                if (AudioDocumentStore.LoadNamed(nameOrPath: args[0]) is not { } document) {
                    return $"[tracker.load: nothing readable at '{args[0]}']";
                }

                scene.Load(document: document);

                return string.Join(separator: '\n', values: (new[] { $"[tracker.load: \"{document.Name}\" ({document.Patterns!.Count} pattern(s), tempo {document.Tempo})]" }).Concat(second: scene.RenderRows()));
            }),
            name: "tracker.load"
        );
        yield return WithArgs(
            description: "Saves the working tune as a puck.audio.v1 document: tracker.save [name] (defaults to the working document's current name).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                var name = ((args.Length > 0) ? args[0] : scene.Document.Name!);

                scene.SetName(name: name);

                return $"[tracker.save: {AudioDocumentStore.SaveNamed(document: scene.Document, name: name)}]";
            }),
            name: "tracker.save"
        );
        yield return Plain(
            description: "Clears the working tune to one blank pattern (the current tune is discarded — save first if you care).",
            handler: WithScene(handler: static scene => {
                scene.New();

                return "[tracker.new: blank tune]";
            }),
            name: "tracker.new"
        );
    }

    // The edit verbs: exact note/tempo control, the same nudges the pad drives.
    private IEnumerable<CommandDefinition> GetEditCommands() {
        yield return WithArgs(
            description: "Sets a row's note directly: tracker.note <row> <note> (a pitch like C5/G#4, or --- to hold, or OFF to cut).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length < 2) || !int.TryParse(s: args[0], result: out var row)) {
                    return "[tracker.note: usage — tracker.note <row> <note>]";
                }

                if (!scene.SetRowNote(row: row, note: args[1])) {
                    return $"[tracker.note: '{args[1]}' is not a recognized note (a pitch C3-C6, ---, or OFF), or row {row} is out of range]";
                }

                // A row change is exactly the "cursor moves or a row changes" trigger the pad path narrates on —
                // the console path shows the same dump for the same reason.
                return string.Join(separator: '\n', values: scene.RenderRows());
            }),
            name: "tracker.note"
        );
        yield return WithArgs(
            description: "Sets the tempo (frames per row): tracker.tempo <n> (1-255).",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && int.TryParse(s: args[0], result: out var tempo))
                ? $"[tracker.tempo: {scene.SetTempo(tempo: tempo)} frame(s)/row]"
                : $"[tracker.tempo: {scene.Document.Tempo} frame(s)/row — give a value 1-255 to change it]")),
            name: "tracker.tempo"
        );
        yield return Plain(
            description: "Prints the current pattern's rows (cursor, document name, tempo).",
            handler: WithScene(handler: static scene => string.Join(separator: '\n', values: scene.RenderRows())),
            name: "tracker.rows"
        );
    }

    // The playback verbs: play/stop preview.
    private IEnumerable<CommandDefinition> GetPlaybackCommands() {
        yield return Plain(
            description: "Starts the preview (compiles the working tune and plays it on a headless machine).",
            handler: _ => (Scene.Active
                ? new CommandResult(ForgeCommands.TrackerRequestPreview(services: services, play: true))
                : new CommandResult("[tracker: enter tracker mode first (console: tracker)]")),
            name: "tracker.play"
        );
        yield return Plain(
            description: "Stops the preview.",
            handler: _ => (Scene.Active
                ? new CommandResult(ForgeCommands.TrackerRequestPreview(services: services, play: false))
                : new CommandResult("[tracker: enter tracker mode first (console: tracker)]")),
            name: "tracker.stop"
        );
    }

    // A no-argument console verb.
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors CreatorCommandModule's
    // WithArgs exactly — usage strings beat parser errors on a game console).
    private CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
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

    // Wraps a scene-editing handler with the availability guard (tracker mode must be entered first).
    private Func<CommandContext, CommandResult> WithScene(Func<TrackerScene, string> handler) {
        return _ => {
            var scene = Scene;

            return (scene.Active
                ? new CommandResult(handler(arg: scene))
                : new CommandResult("[tracker: enter tracker mode first (console: tracker)]"));
        };
    }

    private Func<CommandContext, string[], CommandResult> WithSceneArgs(Func<TrackerScene, string[], string> handler) {
        return (_, args) => {
            var scene = Scene;

            return (scene.Active
                ? new CommandResult(handler(arg1: scene, arg2: args))
                : new CommandResult("[tracker: enter tracker mode first (console: tracker)]"));
        };
    }
}
