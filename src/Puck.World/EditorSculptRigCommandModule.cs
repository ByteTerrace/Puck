using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt TIMELINE + RIG console surface — the assist-layer twins of the LT+RT frames page and the RT+LT
/// rig page: recording/stepping/playing the hold-style frame timeline (the step chord folded onto
/// <c>editor.sculpt.frame</c>), and defining/tuning the IK chains (goal posing rides the target cycle + move stick;
/// these verbs are the named/numeric half — the rig page's define chord folded onto <c>editor.sculpt.chain</c>'s
/// no-argument form). All client-local model state. A SEPARATE module to keep every class under its analyzer
/// ceilings.
/// </summary>
internal sealed class EditorSculptRigCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    /// <summary>The frame-record act (South on the frames page).</summary>
    public const string FrameRecordCommand = "editor.sculpt.frame.record";
    /// <summary>The frame-delete act (D-pad Down on the frames page).</summary>
    public const string FrameRemoveCommand = "editor.sculpt.frame.remove";
    /// <summary>The frame verb, widened to fold the step chord onto itself: <c>editor.sculpt.frame
    /// &lt;n|next|prev&gt;</c>. East/West on the frames page bind it with a constant Axis1D value (+1 next, -1 prev)
    /// in place of an argument.</summary>
    public const string FrameCommand = "editor.sculpt.frame";
    /// <summary>The playback-toggle act (North on the frames page).</summary>
    public const string PlayCommand = "editor.sculpt.play";
    /// <summary>The chain verb: <c>editor.sculpt.chain</c> with no argument defines a LIMB chain from the
    /// selection (South on the rig page — the rig page's own binding);
    /// with arguments it defines a NAMED chain from an explicit shape list.</summary>
    public const string ChainCommand = "editor.sculpt.chain";
    /// <summary>The chain-cursor-cycle act (West on the rig page).</summary>
    public const string ChainNextCommand = "editor.sculpt.chain.next";
    /// <summary>The chain-kind-toggle act (North on the rig page).</summary>
    public const string ChainKindCommand = "editor.sculpt.chain.kind";
    /// <summary>The chain-delete act (East on the rig page): the cursored (or named) chain.</summary>
    public const string ChainRemoveCommand = "editor.sculpt.chain.remove";

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: FrameCommand,
            description: "Moves the timeline cursor and applies that frame's poses (0 = the rest pose): editor.sculpt.frame <n> [seat]. Stepping away from rest captures it first. Also steps by one frame: editor.sculpt.frame <next|prev> [seat] (next; 0 restores rest on prev) — the typed twins of the frames page's East/West D-pad chord, which binds this same verb with no argument, direction from the binding's constant value.",
            handler: FrameHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: FrameRecordCommand,
            description: "RECORDS the current pose: at rest a new frame appends and becomes current; on a saved frame the snapshot overwrites it: editor.sculpt.frame.record [seat]. The chord twin is South on the frames page.",
            handler: FrameRecordHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: FrameRemoveCommand,
            description: "Deletes the CURRENT saved frame (rest is protected; later frames renumber): editor.sculpt.frame.remove [seat]. The chord twin is D-pad Down on the frames page.",
            handler: FrameRemoveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.frame.ticks",
            description: "Sets the playback hold per frame in engine ticks at 60/s (clamped 1..60; the fixed 8-tick cadence is the default): editor.sculpt.frame.ticks <n> [seat].",
            handler: FrameTicksHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: PlayCommand,
            description: "Toggles the hold-style frame-loop playback in the workbench preview (needs at least one saved frame; stopping restores rest): editor.sculpt.play [seat]. The chord twin is North on the frames page.",
            handler: PlayHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ChainCommand,
            description: "With no argument: defines a LIMB chain from the SELECTION — the selected shape as root plus the next 2 shapes in document order (the South chord on the rig page, bound to this verb with no argument): editor.sculpt.chain [seat]. With arguments: defines a NAMED chain from an explicit shape list, in root-to-tip order, capturing their CURRENT positions as the rest geometry: editor.sculpt.chain <name> <shapeIdOrName> <shapeIdOrName> [more...] [limb|spine] — limb (exactly 3 shapes, analytic two-bone) or spine (any length, drag solve; inferred when omitted). The named form acts on the invoking seat's bench (no trailing seat token — the variable member list leaves it ambiguous); the no-argument form takes an optional trailing [seat].",
            handler: ChainHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ChainNextCommand,
            description: "Cycles the rig-page chain CURSOR (kind/delete act on it; wraps through none): editor.sculpt.chain.next [seat]. The chord twin is West on the rig page.",
            handler: ChainNextHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ChainKindCommand,
            description: "Toggles (or sets) a chain's solver kind — limb demotes to spine unless it has exactly 3 shapes: editor.sculpt.chain.kind [limb|spine] [idOrName] [seat] (default: toggle the cursored chain). The chord twin is North on the rig page.",
            handler: ChainKindHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ChainRemoveCommand,
            description: "Deletes a chain: editor.sculpt.chain.remove [idOrName] [seat] (default: the cursored chain). The chord twin is East on the rig page.",
            handler: ChainRemoveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.goal",
            description: "Moves a chain's IK GOAL and re-solves the pose live (the solver writes ordinary shape transforms — record a frame to keep the pose): editor.sculpt.goal <idOrName> <x> <y> <z> [seat]. The stick twin: cycle the target past the shapes onto a goal, then move.",
            handler: GoalHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.pole",
            description: "Sets a limb chain's POLE (the bend-direction hint) and re-solves: editor.sculpt.pole <idOrName> <x> <y> <z> [seat].",
            handler: PoleHandler
        );
    }

    // The frame fold: a leading next|prev literal, or (with no token) a bound constant Axis1D value from the
    // frames page's East/West step chord — see EditorCommandModule.TryDirection. Anything else falls through to
    // the original <n> form.
    private CommandResult FrameHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(context: context, args: args, at: 0, positive: "next", negative: "prev", direction: out var direction)) {
            var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: ((args.Count >= 1) ? 1 : 0), verb: FrameCommand, session: m_session, workbench: m_workbench);

            if (error is { } benchError) {
                return benchError;
            }

            _ = model!.StepFrame(direction: direction);

            return EditorSculptCommandModule.Echo(slot: slot, verb: FrameCommand, detail: $"frame {model.CurrentFrame}/{model.FrameCount}{((model.CurrentFrame == 0) ? " (rest)" : string.Empty)}");
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.frame: expected <n>, next, or prev, plus an optional seat 1..4]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return CommandResult.Error(output: "[editor.sculpt.frame: could not parse <n> as an integer]");
        }

        var (valueSlot, valueModel, valueError) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 1, verb: FrameCommand, session: m_session, workbench: m_workbench);

        if (valueError is { } resolveError) {
            return resolveError;
        }

        valueModel!.SetFrame(index: index);

        return EditorSculptCommandModule.Echo(slot: valueSlot, verb: FrameCommand, detail: $"frame {valueModel.CurrentFrame}/{valueModel.FrameCount}{((valueModel.CurrentFrame == 0) ? " (rest)" : string.Empty)}");
    }
    private CommandResult FrameRecordHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: FrameRecordCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var recorded = model!.RecordFrame();

        return EditorSculptCommandModule.Echo(slot: slot, verb: FrameRecordCommand, detail: $"frame {recorded}/{model.FrameCount} recorded");
    }
    private CommandResult FrameRemoveHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: FrameRemoveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DeleteCurrentFrame()) {
            return CommandResult.Error(output: $"[{FrameRemoveCommand}: the rest pose (frame 0) is protected — step onto a saved frame first]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: FrameRemoveCommand, detail: $"frame removed — {model.FrameCount} left, cursor {model.CurrentFrame}");
    }
    private CommandResult FrameTicksHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.frame.ticks: expected <n 1..60> plus an optional seat 1..4]");
        }

        if (!args.TryInt(index: 0, value: out var ticks)) {
            return CommandResult.Error(output: "[editor.sculpt.frame.ticks: could not parse <n> as an integer]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 1, verb: "editor.sculpt.frame.ticks", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.sculpt.frame.ticks", detail: $"{model!.SetFrameTicks(ticks: ticks)} ticks/frame");
    }
    private CommandResult PlayHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: PlayCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if ((model!.FrameCount == 0)) {
            return CommandResult.Error(output: $"[{PlayCommand}: no saved frames — editor.sculpt.frame.record first]");
        }

        var playing = model.TogglePlayback();

        return EditorSculptCommandModule.Echo(slot: slot, verb: PlayCommand, detail: (playing ? $"playing {model.FrameCount} frames (hold-style)" : "stopped — rest pose restored"));
    }
    // The chain fold: NO ARGUMENT defines a limb from the SELECTION (the rig page's South chord) — bindable,
    // and takes an optional trailing [seat] since there is no variable
    // member list to make one ambiguous. Any argument takes the original named-list form, which deliberately takes
    // no seat token (the variable member list leaves one ambiguous) and so always acts on the invoking seat's bench.
    private CommandResult ChainHandler(CommandContext context, WireArgs args) {
        var isSeatOnly = ((args.Count == 0) || ((args.Count == 1) && EditorSculptCommandModule.SeatToken(token: args[0])));

        if (isSeatOnly) {
            var (noArgSlot, noArgModel, noArgError) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: ChainCommand, session: m_session, workbench: m_workbench);

            if (noArgError is { } resolveError) {
                return resolveError;
            }

            if (noArgModel!.DefineChainFromSelection() is not { } fromSelection) {
                return CommandResult.Error(output: $"[{ChainCommand}: needs a selected shape with 2 more after it in document order (or use editor.sculpt.chain <name> <shapes...>)]");
            }

            return EditorSculptCommandModule.Echo(slot: noArgSlot, verb: ChainCommand, detail: $"chain {fromSelection.Id} ({fromSelection.Kind}, {fromSelection.ShapeIds.Count} shapes)");
        }

        // Shapes: <name> <shape> <shape> [more...] [limb|spine] — acts on the invoking seat's bench (the variable
        // member list makes a trailing seat token ambiguous, so this verb deliberately takes none).
        if (args.Count < 3) {
            return CommandResult.Error(output: "[editor.sculpt.chain: expected <name> <shapeIdOrName> <shapeIdOrName> [more...] [limb|spine]]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: WireArgs.Empty, at: 0, verb: ChainCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var lastIndex = (args.Count - 1);
        var hasKind = (args.Is(index: lastIndex, value: "limb") || args.Is(index: lastIndex, value: "spine"));
        var memberEnd = (hasKind ? lastIndex : args.Count);

        if ((memberEnd - 1) < 2) {
            return CommandResult.Error(output: "[editor.sculpt.chain: a chain needs at least 2 member shapes]");
        }

        var members = new string[(memberEnd - 1)];

        for (var index = 1; (index < memberEnd); index++) {
            members[(index - 1)] = args[index].ToString();
        }

        var chain = model!.DefineChain(name: args[0].ToString(), shapeIdsOrNames: members, kind: (hasKind ? (args.Is(index: lastIndex, value: "limb") ? "limb" : "spine") : null));

        if (chain is null) {
            return CommandResult.Error(output: "[editor.sculpt.chain: could not define — check the shape ids/names (all must resolve) and the 16-chain ceiling]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: ChainCommand, detail: $"chain {chain.Id} '{chain.Name}' ({chain.Kind}, {chain.ShapeIds.Count} shapes) — cycle the target onto its goal to pose it");
    }
    private CommandResult ChainNextHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: ChainNextCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var chain = model!.CycleChainCursor(direction: 1);

        return EditorSculptCommandModule.Echo(slot: slot, verb: ChainNextCommand, detail: ((chain is null) ? "cursor none" : $"cursor chain {chain.Id}{((chain.Name is { Length: > 0 } name) ? $" '{name}'" : string.Empty)} ({chain.Kind})"));
    }
    private CommandResult ChainKindHandler(CommandContext context, WireArgs args) {
        // Shapes: [] = toggle cursored; [limb|spine] [idOrName] [seat].
        var hasKind = ((args.Count >= 1) && (args.Is(index: 0, value: "limb") || args.Is(index: 0, value: "spine")));
        var hasTarget = (hasKind && (args.Count >= 2) && !EditorSculptCommandModule.SeatToken(token: args[1]));
        var seatAt = (hasKind ? (hasTarget ? 2 : 1) : 0);

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: seatAt, verb: ChainKindCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        string? applied;

        if (!hasKind) {
            applied = model!.ToggleCurrentChainKind();

            if (applied is null) {
                return CommandResult.Error(output: $"[{ChainKindCommand}: no chain cursored — {ChainNextCommand} first, or name one: editor.sculpt.chain.kind <limb|spine> <idOrName>]");
            }
        } else {
            var target = (hasTarget ? args[1].ToString() : (model!.CurrentChain?.Id.ToString(provider: CultureInfo.InvariantCulture)));

            if (target is null) {
                return CommandResult.Error(output: $"[{ChainKindCommand}: no chain cursored — {ChainNextCommand} first, or name one]");
            }

            applied = model!.SetKind(idOrName: target, kind: (args.Is(index: 0, value: "limb") ? "limb" : "spine"));

            if (applied is null) {
                return CommandResult.Error(output: $"[{ChainKindCommand}: no chain '{target}']");
            }
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: ChainKindCommand, detail: $"kind {applied}");
    }
    private CommandResult ChainRemoveHandler(CommandContext context, WireArgs args) {
        var hasTarget = ((args.Count >= 1) && !EditorSculptCommandModule.SeatToken(token: args[0]));

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: (hasTarget ? 1 : 0), verb: ChainRemoveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var target = (hasTarget ? args[0].ToString() : (model!.CurrentChain?.Id.ToString(provider: CultureInfo.InvariantCulture)));

        if (target is null) {
            return CommandResult.Error(output: $"[{ChainRemoveCommand}: no chain cursored — {ChainNextCommand} first, or name one]");
        }

        if (!model!.DeleteChain(idOrName: target)) {
            return CommandResult.Error(output: $"[{ChainRemoveCommand}: no chain '{target}']");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: ChainRemoveCommand, detail: $"chain removed — {model.Chains.Count} left");
    }
    private CommandResult GoalHandler(CommandContext context, WireArgs args) =>
        ChainPointHandler(context: context, args: in args, verb: "editor.sculpt.goal", isGoal: true);
    private CommandResult PoleHandler(CommandContext context, WireArgs args) =>
        ChainPointHandler(context: context, args: in args, verb: "editor.sculpt.pole", isGoal: false);

    // The shared <idOrName> <x y z> [seat] handler for goal/pole moves.
    private CommandResult ChainPointHandler(CommandContext context, in WireArgs args, string verb, bool isGoal) {
        if (args.Count is (< 4 or > 5)) {
            return CommandResult.Error(output: $"[{verb}: expected <idOrName> <x> <y> <z> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(args: in args, at: 1, value: out var x) ||
            !EditorCommandModule.TryFloat(args: in args, at: 2, value: out var y) ||
            !EditorCommandModule.TryFloat(args: in args, at: 3, value: out var z)) {
            return CommandResult.Error(output: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 4, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var idOrName = args[0].ToString();
        var point = new Vector3(x: x, y: y, z: z);
        var applied = (isGoal
            ? model!.SetGoal(idOrName: idOrName, goal: point)
            : model!.SetPole(idOrName: idOrName, pole: point));

        if (!applied) {
            return CommandResult.Error(output: $"[{verb}: no chain '{idOrName}']");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"chain '{idOrName}' {(isGoal ? "goal" : "pole")}=({x:0.00}, {y:0.00}, {z:0.00}) — pose re-solved"
        ));
    }

}
