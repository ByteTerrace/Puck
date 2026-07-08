using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Forge;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's console-assist verbs — the precise/named half of the pad-first + console-assist input model,
/// mirroring <see cref="Creator.CreatorCommandModule"/>'s shape exactly. Open the backtick console and type
/// <c>tracker.rows</c> to see the current pattern; every other verb here gives the pad's nudges exact, named
/// control (load/save a document by name, set a row's note directly, set the tempo, start/stop the preview).
/// </summary>
/// <remarks>
/// WHY THIS MODULE KEEPS <c>Tracker.TrackerScene</c> OFF THE RENDER NODE'S SURFACE.
/// <see cref="Overworld.OverworldRenderNode"/> is AT its analyzer coupling ceiling already (its own remarks say
/// so — see <c>ICreatorModeHost</c>'s doc comment there), so <c>Tracker.TrackerModeState</c>/<c>TrackerScene</c>
/// must never appear in its signature; they live behind a lazily-built static singleton
/// (<see cref="ForgeCommands.TrackerModeInstance"/>) reached through <see cref="IServiceProvider"/>, and every edit
/// verb here drives that singleton's <see cref="TrackerScene"/> directly (never through the node). The module also
/// takes <c>IRenderNode</c> — but ONLY to reach the PRIMITIVE-typed authoring seam (<c>ICreatorModeHost</c>, the
/// same lazy-through-the-node pattern <see cref="ConditionCommandModule"/> and <c>Creator.CreatorCommandModule</c>
/// use) for the two arg-less string-returning forwarders <c>ToggleTrackerMode</c> / <c>RequestTrackerPreview</c> /
/// <c>RequestTuneForge</c>. That adds NO Tracker type to the node's surface — the coupling concern is Tracker's OWN
/// row/note/tempo state (kept behind the singleton), not the node reference itself.
/// </remarks>
internal sealed class TrackerCommandModule(IServiceProvider services, IRenderNode rootNode) : ICommandModule {
    private TrackerScene Scene => ForgeCommands.TrackerModeInstance(services: services).Scene;

    // The overworld root, cast to the primitive-typed authoring seam — the same lazy-through-the-node pattern
    // ConditionCommandModule uses. RequestTuneForge is a PRIMITIVE (string-returning, arg-less) method on
    // ICreatorModeHost, so taking IRenderNode here adds NO Tracker type to the node's surface (the module's remarks'
    // concern) — it only routes the tune-forge intent to the node's forge queue. Null for a non-overworld root.
    private readonly ICreatorModeHost? m_creatorHost = (rootNode as ICreatorModeHost);

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

                try {
                    if (AudioDocumentStore.LoadNamed(nameOrPath: args[0]) is not { } document) {
                        return $"[tracker.load: nothing readable at '{args[0]}']";
                    }

                    scene.Load(document: document);

                    return string.Join(separator: '\n', values: (new[] { $"[tracker.load: \"{document.Name}\" ({document.Patterns!.Count} pattern(s), tempo {document.Tempo})]" }).Concat(second: scene.RenderRows()));
                }
                catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                    return $"[tracker.load: '{args[0]}' is unreadable — {exception.Message}]";
                }
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
                if ((args.Length < 2) || !TryParseInt(text: args[0], value: out var row)) {
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
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseInt(text: args[0], value: out var tempo))
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
        yield return Plain(
            description: "FORGES the working tune into a JUKEBOX cart (GPU-free) and hot-swaps it into the nearest cabinet in-session — the tune half of the subject-neutral author→forge→hot-swap loop. Enter tracker mode and author a tune first; Cycle/boot a cabinet to it to hear the loop.",
            handler: _ => new CommandResult((m_creatorHost is null)
                ? "[tracker.forge: unavailable — the overworld is not the active root]"
                : m_creatorHost.RequestTuneForge()),
            name: "tracker.forge"
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

    // Wraps a scene-editing handler with the shared availability guard (CommandAvailability): Scene is never null
    // (ForgeCommands.TrackerModeInstance builds it lazily on first touch), so only the active gate applies —
    // tracker mode must be entered first (TrackerScene.Active), mirroring CreatorCommandModule's active-gate shape.
    private Func<CommandContext, CommandResult> WithScene(Func<TrackerScene, string> handler) =>
        CommandAvailability.WithTarget(
            getTarget: () => Scene,
            handler: handler,
            isActive: static scene => scene.Active,
            inactiveMessage: "[tracker: enter tracker mode first (console: tracker)]",
            unavailableMessage: "[tracker: enter tracker mode first (console: tracker)]"
        );

    private Func<CommandContext, string[], CommandResult> WithSceneArgs(Func<TrackerScene, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(
            getTarget: () => Scene,
            handler: handler,
            isActive: static scene => scene.Active,
            inactiveMessage: "[tracker: enter tracker mode first (console: tracker)]",
            unavailableMessage: "[tracker: enter tracker mode first (console: tracker)]"
        );
}
