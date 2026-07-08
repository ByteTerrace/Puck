using System.CommandLine;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using Puck.SdfVm;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo.Creator;

/// <summary>
/// The editor's console-assist verbs — the precise/named half of the pad-first + console-assist input model. Open the
/// backtick console and type <c>creator.help</c> for the tour: naming, saving/loading creations as
/// <c>puck.creation.v1</c> documents, exact transforms, palette edits, blend ops, grouping, and the intent/style
/// knobs. Every verb targets the SELECTED shape (else the ghost), mirroring the pad's target model; editing verbs
/// require creator mode to be up.
/// </summary>
internal sealed class CreatorCommandModule(IRenderNode rootNode) : ICommandModule {
    private readonly ICreatorModeHost? m_host = (rootNode as ICreatorModeHost);

    private CreatorScene? Scene => m_host?.CreatorFrameSource?.Creator;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in GetDocumentCommands()) { yield return command; }
        foreach (var command in GetShapeCommands()) { yield return command; }
        foreach (var command in GetStyleCommands()) { yield return command; }
        foreach (var command in GetTransformCommands()) { yield return command; }
        foreach (var command in GetTimelineCommands()) { yield return command; }
        foreach (var command in CreatorRigCommands.GetCommands(module: this)) { yield return command; }
    }

    // The document verbs: save/load/list/new + the creation-level knobs.
    private IEnumerable<CommandDefinition> GetDocumentCommands() {
        yield return Plain(
            description: "Lists the saved creations under ./creations/.",
            handler: _ => new CommandResult((CreationStore.List() is { Count: > 0 } names)
                ? $"[creator.list: {string.Join(separator: ", ", values: names)}]"
                : "[creator.list: none saved yet — creator.save <name> writes one]"),
            name: "creator.list"
        );
        yield return Plain(
            description: "Clears the creator scene to empty (the shapes are discarded — save first if you care).",
            handler: WithScene(handler: scene => $"[creator.new: discarded {scene.Clear()} shape(s)]"),
            name: "creator.new"
        );
        yield return Plain(
            description: "Places the ghost's current primitive into the scene — the console/stdin equivalent of the pad's South (a no-op when the pool is full or creator is not editing). Style + move the ghost first (creator.material/op/smooth/move), then place.",
            handler: WithScene(handler: static scene => {
                var before = scene.PlacedCount;

                scene.Place();

                return ((scene.PlacedCount > before)
                    ? $"[creator.place: placed — {scene.PlacedCount} shape(s)]"
                    : "[creator.place: no-op (pool full, or not editing)]");
            }),
            name: "creator.place"
        );
        yield return WithArgs(
            description: "Saves the creation as a puck.creation.v1 document: creator.save <name> (defaults to the creation's current name).",
            handler: WithSceneArgs(handler: (scene, args) => {
                var name = ((args.Length > 0) ? args[0] : scene.Name);

                scene.SetName(name: name);

                return $"[creator.save: {CreationStore.Save(document: scene.ToDocument(), name: name)}]";
            }),
            name: "creator.save"
        );
        yield return WithArgs(
            description: "Loads a saved creation (or an explicit file path; legacy .avatar.json imports too): creator.load <name>.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (args.Length == 0) {
                    return "[creator.load: give a name — creator.list shows what's saved]";
                }

                try {
                    return ((CreationStore.Load(nameOrPath: args[0]) is { } document)
                        ? $"[creator.load: {scene.LoadDocument(document: document)} shape(s) from '{document.Name}' (style {document.BakeStyle}, intent {document.Intent})]"
                        : $"[creator.load: nothing readable at '{args[0]}']");
                }
                catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                    return $"[creator.load: '{args[0]}' is unreadable — {exception.Message}]";
                }
            }),
            name: "creator.load"
        );
        yield return WithArgs(
            description: "Sets the authoring intent: creator.intent <object|sprite> (sprite = head-on framing for bake-bound art).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length == 0) || !Enum.TryParse<CreatorIntent>(ignoreCase: true, result: out var intent, value: args[0])) {
                    return "[creator.intent: object or sprite]";
                }

                scene.SetIntent(intent: intent);

                return $"[creator.intent: {intent}]";
            }),
            name: "creator.intent"
        );
        yield return WithArgs(
            description: "Sets the bake style knob: creator.style <classic|bold>.",
            handler: WithSceneArgs(handler: static (scene, args) => ((args.Length > 0)
                ? $"[creator.style: {scene.SetBakeStyle(style: args[0])}]"
                : $"[creator.style: {scene.BakeStyle} — give classic or bold to change it]")),
            name: "creator.style"
        );
    }

    // The shape verbs: selection, naming, exact transforms, grouping.
    private IEnumerable<CommandDefinition> GetShapeCommands() {
        yield return WithArgs(
            description: "Selects a placed shape by id or name: creator.select <id|name>.",
            handler: WithSceneArgs(handler: static (scene, args) => ((args.Length == 0)
                ? "[creator.select: give an id or name]"
                : ((scene.Select(idOrName: args[0]) is { } shape)
                    ? $"[creator.select: #{shape.Id} {shape.Name ?? shape.Type.ToString()}{((shape.GroupId != 0) ? $" (group {shape.GroupId})" : "")}]"
                    : $"[creator.select: no shape matches '{args[0]}']"))),
            name: "creator.select"
        );
        yield return WithArgs(
            description: "Names the SELECTED shape (no selection: names the whole creation): creator.name <name>.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (args.Length == 0) {
                    return "[creator.name: give a name]";
                }

                if (scene.RenameSelected(name: args[0])) {
                    return $"[creator.name: shape renamed to '{args[0]}']";
                }

                scene.SetName(name: args[0]);

                return $"[creator.name: creation renamed to '{args[0]}' (select a shape first to name it)]";
            }),
            name: "creator.name"
        );
    }

    // The style verbs: palette slots, palette entries, blend ops, smoothness.
    private IEnumerable<CommandDefinition> GetStyleCommands() {
        yield return WithArgs(
            description: "Assigns the target's palette slot: creator.material <0-15>.",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseInt(text: args[0], value: out var index))
                ? $"[creator.material: palette slot {scene.SetMaterialIndex(index: index)}]"
                : "[creator.material: give a palette slot 0-15]")),
            name: "creator.material"
        );
        yield return WithArgs(
            description: "Edits a palette entry: creator.palette <slot> <r> <g> <b> [emissive] [specular] [shininess] (0-1 floats; shininess is an exponent).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length < 4) ||
                    !TryParseInt(text: args[0], value: out var slot) ||
                    !TryParseFloats(args: args, count: 3, start: 1, values: out var rgb)) {
                    return "[creator.palette: usage — creator.palette <slot> <r> <g> <b> [emissive] [specular] [shininess]]";
                }

                var defaults = new SdfMaterial(Albedo: new Vector3(rgb[0], rgb[1], rgb[2]));

                scene.SetPaletteEntry(index: slot, material: (defaults with {
                    Emissive = (((args.Length > 4) && TryParseFloat(text: args[4], value: out var emissive)) ? emissive : defaults.Emissive),
                    Specular = (((args.Length > 5) && TryParseFloat(text: args[5], value: out var specular)) ? specular : defaults.Specular),
                    Shininess = (((args.Length > 6) && TryParseFloat(text: args[6], value: out var shininess)) ? shininess : defaults.Shininess),
                }));

                return $"[creator.palette: slot {slot} = ({rgb[0]:F2}, {rgb[1]:F2}, {rgb[2]:F2})]";
            }),
            name: "creator.palette"
        );
        yield return WithArgs(
            description: "Sets the target's blend op: creator.op <union|smoothunion|subtract|smoothsubtract|intersect|smoothintersect|xor> (non-union auto-groups an ungrouped shape).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length == 0) || (ParseBlend(name: args[0]) is not { } blend)) {
                    return "[creator.op: one of union, smoothunion, subtract, smoothsubtract, intersect, smoothintersect, xor]";
                }

                scene.SetBlend(blend: blend);

                return $"[creator.op: {blend}{((scene.SelectedShape is { GroupId: not 0 } shape) ? $" (group {shape.GroupId})" : "")}]";
            }),
            name: "creator.op"
        );
        yield return WithArgs(
            description: "Sets the target's smooth-blend radius: creator.smooth <0-0.5>.",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseFloat(text: args[0], value: out var radius))
                ? $"[creator.smooth: {scene.SetSmooth(value: radius):F3}]"
                : "[creator.smooth: give a radius 0-0.5]")),
            name: "creator.smooth"
        );
        yield return WithArgs(
            description: "Sets the bake preview's hardware target: creator.baketarget <dmg|cgb>.",
            handler: WithSceneArgs(handler: static (scene, args) => ((args.Length > 0)
                ? $"[creator.baketarget: {scene.SetBakeTarget(target: args[0])}]"
                : $"[creator.baketarget: {scene.BakeTargetName} — give dmg or cgb to change it]")),
            name: "creator.baketarget"
        );
        yield return WithArgs(
            description: "Sets the bake preview's overlay: creator.bakeoverlay <0|1|2> (0 bare, 1 palette strip + warning ticks, 2 + tile grid).",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseInt(text: args[0], value: out var mode))
                ? $"[creator.bakeoverlay: {scene.SetBakeOverlay(mode: mode)}]"
                : $"[creator.bakeoverlay: {scene.BakeOverlay} — give 0, 1, or 2]")),
            name: "creator.bakeoverlay"
        );
        yield return Plain(
            description: "Toggles the target's mirror flag (SymmetryX — the shape's field mirrors across its local X=0 plane).",
            handler: WithScene(handler: static scene => $"[creator.mirror: {(scene.ToggleMirror() ? "on" : "off")}]"),
            name: "creator.mirror"
        );
        yield return WithArgs(
            description: $"Sets the target's twist rate directly: creator.twist <rate> (±{CreatorScene.MaxTwist:F1} rad/unit).",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseFloat(text: args[0], value: out var rate))
                ? $"[creator.twist: {scene.SetTwist(value: rate):F2}]"
                : $"[creator.twist: give a rate ±{CreatorScene.MaxTwist:F1}]")),
            name: "creator.twist"
        );
        yield return WithArgs(
            description: $"Sets the target's onion shell thickness directly: creator.onion <thickness> (0-{CreatorScene.MaxOnion:F2}).",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseFloat(text: args[0], value: out var thickness))
                ? $"[creator.onion: {scene.SetOnion(value: thickness):F3}]"
                : $"[creator.onion: give a thickness 0-{CreatorScene.MaxOnion:F2}]")),
            name: "creator.onion"
        );
    }

    // The exact-transform + grouping verbs.
    private IEnumerable<CommandDefinition> GetTransformCommands() {
        yield return WithArgs(
            description: "Places the target at an exact position: creator.move <x> <y> <z> (clamped to the workbench).",
            handler: WithSceneArgs(handler: static (scene, args) => (TryParseFloats(args: args, count: 3, start: 0, values: out var xyz)
                ? $"[creator.move: {Describe(vector: scene.SetTargetPosition(position: new Vector3(xyz[0], xyz[1], xyz[2])))}]"
                : "[creator.move: usage — creator.move <x> <y> <z>]")),
            name: "creator.move"
        );
        yield return WithArgs(
            description: "Sets the target's orientation in degrees: creator.rot <yaw> <pitch> <roll>.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (!TryParseFloats(args: args, count: 3, start: 0, values: out var ypr)) {
                    return "[creator.rot: usage — creator.rot <yaw°> <pitch°> <roll°>]";
                }

                scene.SetTargetRotation(pitchDegrees: ypr[1], rollDegrees: ypr[2], yawDegrees: ypr[0]);

                return $"[creator.rot: yaw {ypr[0]:F0}° pitch {ypr[1]:F0}° roll {ypr[2]:F0}°]";
            }),
            name: "creator.rot"
        );
        yield return WithArgs(
            description: "Sets the target's scale, per-axis when three values are given: creator.scale <x> [y z] (0.2-3.0).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (TryParseFloats(args: args, count: 3, start: 0, values: out var xyz)) {
                    return $"[creator.scale: {Describe(vector: scene.SetTargetScale(scale: new Vector3(xyz[0], xyz[1], xyz[2])))}]";
                }

                if ((args.Length > 0) && TryParseFloat(text: args[0], value: out var uniform)) {
                    return $"[creator.scale: {Describe(vector: scene.SetTargetScale(scale: new Vector3(uniform)))}]";
                }

                return "[creator.scale: usage — creator.scale <x> [y z]]";
            }),
            name: "creator.scale"
        );
        yield return Plain(
            description: "Groups the SELECTED shape with the PREVIOUSLY selected one (select one, select the other, then group).",
            handler: WithScene(handler: static scene => ((scene.LinkWithPrevious() is { } group)
                ? $"[creator.group: joined group {group}]"
                : "[creator.group: needs two selections — creator.select one, creator.select the other, then group]")),
            name: "creator.group"
        );
        yield return Plain(
            description: "Dissolves the SELECTED shape's group (members return to ungrouped plain-union shapes).",
            handler: WithScene(handler: static scene => ((scene.UngroupTarget() is > 0 and var released)
                ? $"[creator.ungroup: released {released} shape(s)]"
                : "[creator.ungroup: the target is not in a group]")),
            name: "creator.ungroup"
        );
        yield return Plain(
            description: "Undoes the last completed edit (shapes, palette, frames, chains — the whole authored state).",
            handler: WithScene(handler: static scene => (scene.Undo()
                ? $"[creator.undo: {scene.PlacedCount} shape(s)]"
                : "[creator.undo: nothing to undo]")),
            name: "creator.undo"
        );
        yield return Plain(
            description: "Redoes the last undone edit (a no-op after a new edit truncated the redo tail).",
            handler: WithScene(handler: static scene => (scene.Redo()
                ? $"[creator.redo: {scene.PlacedCount} shape(s)]"
                : "[creator.redo: nothing to redo]")),
            name: "creator.redo"
        );
    }

    // The timeline verbs (the frame-snapshot animation model).
    private IEnumerable<CommandDefinition> GetTimelineCommands() {
        yield return WithArgs(
            description: "Drives the timeline: creator.frame <n> jumps to a frame (0 = rest), creator.frame add records the pose, creator.frame del deletes the current frame.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (args.Length == 0) {
                    return $"[creator.frame: {scene.CurrentFrame} of {scene.FrameCount} (0 = rest)]";
                }

                if (string.Equals(a: args[0], b: "add", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return $"[creator.frame: recorded frame {scene.RecordFrame()} of {scene.FrameCount}]";
                }

                if (string.Equals(a: args[0], b: "del", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return (scene.DeleteCurrentFrame()
                        ? $"[creator.frame: deleted — {scene.FrameCount} remain]"
                        : "[creator.frame: rest cannot be deleted]");
                }

                if (TryParseInt(text: args[0], value: out var index)) {
                    scene.SetFrame(index: index);

                    return $"[creator.frame: {scene.CurrentFrame} of {scene.FrameCount}]";
                }

                return "[creator.frame: usage — creator.frame <n|add|del>]";
            }),
            name: "creator.frame"
        );
        yield return Plain(
            description: "Plays/stops the timeline's frame loop on the workbench.",
            handler: WithScene(handler: static scene => (scene.TogglePlayback()
                ? $"[creator.play: {scene.FrameCount} frame(s) looping]"
                : ((scene.FrameCount == 0) ? "[creator.play: no frames — creator.frame add records one]" : "[creator.play: stopped]"))),
            name: "creator.play"
        );
        yield return Plain(
            description: "Stops timeline playback and restores the rest pose.",
            handler: WithScene(handler: static scene => {
                scene.StopPlayback();

                return "[creator.stop: rest pose]";
            }),
            name: "creator.stop"
        );
        yield return WithArgs(
            description: "Sets the playback hold per frame in ticks at 60/s: creator.anim <1-60> (default 8 ≈ 133 ms).",
            handler: WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && TryParseInt(text: args[0], value: out var ticks))
                ? $"[creator.anim: {scene.SetFrameTicks(ticks: ticks)} tick(s) per frame]"
                : "[creator.anim: give a tick count 1-60]")),
            name: "creator.anim"
        );
    }

    // A no-argument console verb. Internal (not private): CreatorRigCommands (a separate file, kept small to stay
    // under the analyzer's coupling/complexity ceilings) reuses this + the wrappers below via the module instance.
    internal static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (uniform + forgiving — usage
    // strings beat parser errors on a game console).
    internal CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
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

    // Wraps a scene-editing handler with the shared availability guard (CommandAvailability): the overworld root
    // must be up (host gate) AND creator mode must be entered (active gate — CreatorScene.Active).
    internal Func<CommandContext, CommandResult> WithScene(Func<CreatorScene, string> handler) =>
        CommandAvailability.WithTarget(
            getTarget: () => Scene,
            handler: handler,
            isActive: static scene => scene.Active,
            inactiveMessage: "[creator: enter creator mode first (console: creator)]",
            unavailableMessage: "[creator: unavailable — the overworld is not the active root]"
        );

    internal Func<CommandContext, string[], CommandResult> WithSceneArgs(Func<CreatorScene, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(
            getTarget: () => Scene,
            handler: handler,
            isActive: static scene => scene.Active,
            inactiveMessage: "[creator: enter creator mode first (console: creator)]",
            unavailableMessage: "[creator: unavailable — the overworld is not the active root]"
        );

    private static SdfBlendOp? ParseBlend(string name) {
        return name.ToLowerInvariant() switch {
            "union" => SdfBlendOp.Union,
            "smoothunion" => SdfBlendOp.SmoothUnion,
            "subtract" or "subtraction" => SdfBlendOp.Subtraction,
            "smoothsubtract" or "smoothsubtraction" => SdfBlendOp.SmoothSubtraction,
            "intersect" or "intersection" => SdfBlendOp.Intersection,
            "smoothintersect" or "smoothintersection" => SdfBlendOp.SmoothIntersection,
            "xor" => SdfBlendOp.Xor,
            _ => null,
        };
    }

    internal static string Describe(Vector3 vector) =>
        $"({vector.X:F2}, {vector.Y:F2}, {vector.Z:F2})";
}
