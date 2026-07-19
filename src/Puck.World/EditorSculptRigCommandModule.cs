using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt TIMELINE + RIG console surface — the assist-layer twins of the LT+RT frames page and the RT+LT
/// rig page: recording/stepping/playing the hold-style frame timeline, and defining/tuning the IK chains (goal
/// posing rides the target cycle + move stick; these verbs are the named/numeric half). All client-local model
/// state. A SEPARATE module to keep every class under its analyzer ceilings.
/// </summary>
internal sealed class EditorSculptRigCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    /// <summary>The frame-record act (South on the frames page).</summary>
    public const string FrameRecordCommand = "editor.sculpt.frame.record";
    /// <summary>The frame-delete act (D-pad Down on the frames page).</summary>
    public const string FrameRemoveCommand = "editor.sculpt.frame.remove";
    /// <summary>The frame-step-forward act (East on the frames page).</summary>
    public const string FrameNextCommand = "editor.sculpt.frame.next";
    /// <summary>The frame-step-back act (West on the frames page).</summary>
    public const string FramePrevCommand = "editor.sculpt.frame.prev";
    /// <summary>The playback-toggle act (North on the frames page).</summary>
    public const string PlayCommand = "editor.sculpt.play";
    /// <summary>The chain-define act (South on the rig page): a limb from the selection's 3-shape run.</summary>
    public const string ChainDefineCommand = "editor.sculpt.chain.define";
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
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.frame",
            description: "Moves the timeline cursor and applies that frame's poses (0 = the rest pose): editor.sculpt.frame <n> [seat]. Stepping away from rest captures it first.",
            handler: FrameHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: FrameNextCommand,
            description: "Steps the timeline cursor forward one frame: editor.sculpt.frame.next [seat]. The chord twin is East on the frames page.",
            handler: (context, args) => FrameStepHandler(context: context, args: args, direction: 1, verb: FrameNextCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: FramePrevCommand,
            description: "Steps the timeline cursor back one frame (0 restores rest): editor.sculpt.frame.prev [seat]. The chord twin is West on the frames page.",
            handler: (context, args) => FrameStepHandler(context: context, args: args, direction: -1, verb: FramePrevCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: FrameRecordCommand,
            description: "RECORDS the current pose: at rest a new frame appends and becomes current; on a saved frame the snapshot overwrites it: editor.sculpt.frame.record [seat]. The chord twin is South on the frames page.",
            handler: FrameRecordHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: FrameRemoveCommand,
            description: "Deletes the CURRENT saved frame (rest is protected; later frames renumber): editor.sculpt.frame.remove [seat]. The chord twin is D-pad Down on the frames page.",
            handler: FrameRemoveHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.frame.ticks",
            description: "Sets the playback hold per frame in engine ticks at 60/s (clamped 1..60; the fixed 8-tick cadence is the default): editor.sculpt.frame.ticks <n> [seat].",
            handler: FrameTicksHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PlayCommand,
            description: "Toggles the hold-style frame-loop playback in the workbench preview (needs at least one saved frame; stopping restores rest): editor.sculpt.play [seat]. The chord twin is North on the frames page.",
            handler: PlayHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.chain",
            description: "Defines an IK chain from shapes in root-to-tip order, capturing their CURRENT positions as the rest geometry: editor.sculpt.chain <name> <shapeIdOrName> <shapeIdOrName> [more...] [limb|spine] — limb (exactly 3 shapes, analytic two-bone) or spine (any length, drag solve; inferred when omitted). Acts on the invoking seat's bench.",
            handler: ChainHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ChainDefineCommand,
            description: "Defines a LIMB chain from the selection: the selected shape as root plus the next 2 shapes in document order: editor.sculpt.chain.define [seat]. The chord twin is South on the rig page.",
            handler: ChainDefineHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ChainNextCommand,
            description: "Cycles the rig-page chain CURSOR (kind/delete act on it; wraps through none): editor.sculpt.chain.next [seat]. The chord twin is West on the rig page.",
            handler: ChainNextHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ChainKindCommand,
            description: "Toggles (or sets) a chain's solver kind — limb demotes to spine unless it has exactly 3 shapes: editor.sculpt.chain.kind [limb|spine] [idOrName] [seat] (default: toggle the cursored chain). The chord twin is North on the rig page.",
            handler: ChainKindHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ChainRemoveCommand,
            description: "Deletes a chain: editor.sculpt.chain.remove [idOrName] [seat] (default: the cursored chain). The chord twin is East on the rig page.",
            handler: ChainRemoveHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.goal",
            description: "Moves a chain's IK GOAL and re-solves the pose live (the solver writes ordinary shape transforms — record a frame to keep the pose): editor.sculpt.goal <idOrName> <x> <y> <z> [seat]. The stick twin: cycle the target past the shapes onto a goal, then move.",
            handler: GoalHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.pole",
            description: "Sets a limb chain's POLE (the bend-direction hint) and re-solves: editor.sculpt.pole <idOrName> <x> <y> <z> [seat].",
            handler: PoleHandler
        );
    }

    private CommandResult FrameHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.frame: expected <n> plus an optional seat 1..4]");
        }

        if (!int.TryParse(s: args[0], provider: CultureInfo.InvariantCulture, result: out var index)) {
            return Error(text: "[editor.sculpt.frame: could not parse <n> as an integer]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 1, verb: "editor.sculpt.frame", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        model!.SetFrame(index: index);

        return Echo(slot: slot, verb: "editor.sculpt.frame", detail: $"frame {model.CurrentFrame}/{model.FrameCount}{((model.CurrentFrame == 0) ? " (rest)" : string.Empty)}");
    }

    private CommandResult FrameStepHandler(CommandContext context, string[] args, int direction, string verb) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        _ = model!.StepFrame(direction: direction);

        return Echo(slot: slot, verb: verb, detail: $"frame {model.CurrentFrame}/{model.FrameCount}{((model.CurrentFrame == 0) ? " (rest)" : string.Empty)}");
    }

    private CommandResult FrameRecordHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: FrameRecordCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var recorded = model!.RecordFrame();

        return Echo(slot: slot, verb: FrameRecordCommand, detail: $"frame {recorded}/{model.FrameCount} recorded");
    }

    private CommandResult FrameRemoveHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: FrameRemoveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DeleteCurrentFrame()) {
            return Error(text: $"[{FrameRemoveCommand}: the rest pose (frame 0) is protected — step onto a saved frame first]");
        }

        return Echo(slot: slot, verb: FrameRemoveCommand, detail: $"frame removed — {model.FrameCount} left, cursor {model.CurrentFrame}");
    }

    private CommandResult FrameTicksHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.frame.ticks: expected <n 1..60> plus an optional seat 1..4]");
        }

        if (!int.TryParse(s: args[0], provider: CultureInfo.InvariantCulture, result: out var ticks)) {
            return Error(text: "[editor.sculpt.frame.ticks: could not parse <n> as an integer]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 1, verb: "editor.sculpt.frame.ticks", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        return Echo(slot: slot, verb: "editor.sculpt.frame.ticks", detail: $"{model!.SetFrameTicks(ticks: ticks)} ticks/frame");
    }

    private CommandResult PlayHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: PlayCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if ((model!.FrameCount == 0)) {
            return Error(text: $"[{PlayCommand}: no saved frames — editor.sculpt.frame.record first]");
        }

        var playing = model.TogglePlayback();

        return Echo(slot: slot, verb: PlayCommand, detail: (playing ? $"playing {model.FrameCount} frames (hold-style)" : "stopped — rest pose restored"));
    }

    private CommandResult ChainHandler(CommandContext context, string[] args) {
        // Shapes: <name> <shape> <shape> [more...] [limb|spine] — acts on the invoking seat's bench (the variable
        // member list makes a trailing seat token ambiguous, so this verb deliberately takes none).
        if (args.Length < 3) {
            return Error(text: "[editor.sculpt.chain: expected <name> <shapeIdOrName> <shapeIdOrName> [more...] [limb|spine]]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: [], at: 0, verb: "editor.sculpt.chain", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var last = args[^1];
        var hasKind = (string.Equals(a: last, b: "limb", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a: last, b: "spine", comparisonType: StringComparison.OrdinalIgnoreCase));
        var memberEnd = (hasKind ? (args.Length - 1) : args.Length);

        if ((memberEnd - 1) < 2) {
            return Error(text: "[editor.sculpt.chain: a chain needs at least 2 member shapes]");
        }

        var chain = model!.DefineChain(name: args[0], shapeIdsOrNames: args[1..memberEnd], kind: (hasKind ? last.ToLowerInvariant() : null));

        if (chain is null) {
            return Error(text: "[editor.sculpt.chain: could not define — check the shape ids/names (all must resolve) and the 16-chain ceiling]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.chain", detail: $"chain {chain.Id} '{chain.Name}' ({chain.Kind}, {chain.ShapeIds.Count} shapes) — cycle the target onto its goal to pose it");
    }

    private CommandResult ChainDefineHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: ChainDefineCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (model!.DefineChainFromSelection() is not { } chain) {
            return Error(text: $"[{ChainDefineCommand}: needs a selected shape with 2 more after it in document order (or use editor.sculpt.chain <name> <shapes...>)]");
        }

        return Echo(slot: slot, verb: ChainDefineCommand, detail: $"chain {chain.Id} ({chain.Kind}, {chain.ShapeIds.Count} shapes)");
    }

    private CommandResult ChainNextHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: ChainNextCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var chain = model!.CycleChainCursor(direction: 1);

        return Echo(slot: slot, verb: ChainNextCommand, detail: ((chain is null) ? "cursor none" : $"cursor chain {chain.Id}{((chain.Name is { Length: > 0 } name) ? $" '{name}'" : string.Empty)} ({chain.Kind})"));
    }

    private CommandResult ChainKindHandler(CommandContext context, string[] args) {
        // Shapes: [] = toggle cursored; [limb|spine] [idOrName] [seat].
        var hasKind = ((args.Length >= 1) &&
            (string.Equals(a: args[0], b: "limb", comparisonType: StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a: args[0], b: "spine", comparisonType: StringComparison.OrdinalIgnoreCase)));
        var hasTarget = (hasKind && (args.Length >= 2) && !SeatToken(token: args[1]));
        var seatAt = (hasKind ? (hasTarget ? 2 : 1) : 0);
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: seatAt, verb: ChainKindCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        string? applied;

        if (!hasKind) {
            applied = model!.ToggleCurrentChainKind();

            if (applied is null) {
                return Error(text: $"[{ChainKindCommand}: no chain cursored — {ChainNextCommand} first, or name one: editor.sculpt.chain.kind <limb|spine> <idOrName>]");
            }
        } else {
            var target = (hasTarget ? args[1] : (model!.CurrentChain?.Id.ToString(provider: CultureInfo.InvariantCulture)));

            if (target is null) {
                return Error(text: $"[{ChainKindCommand}: no chain cursored — {ChainNextCommand} first, or name one]");
            }

            applied = model!.SetKind(idOrName: target, kind: args[0].ToLowerInvariant());

            if (applied is null) {
                return Error(text: $"[{ChainKindCommand}: no chain '{target}']");
            }
        }

        return Echo(slot: slot, verb: ChainKindCommand, detail: $"kind {applied}");
    }

    private CommandResult ChainRemoveHandler(CommandContext context, string[] args) {
        var hasTarget = ((args.Length >= 1) && !SeatToken(token: args[0]));
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: (hasTarget ? 1 : 0), verb: ChainRemoveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var target = (hasTarget ? args[0] : (model!.CurrentChain?.Id.ToString(provider: CultureInfo.InvariantCulture)));

        if (target is null) {
            return Error(text: $"[{ChainRemoveCommand}: no chain cursored — {ChainNextCommand} first, or name one]");
        }

        if (!model!.DeleteChain(idOrName: target)) {
            return Error(text: $"[{ChainRemoveCommand}: no chain '{target}']");
        }

        return Echo(slot: slot, verb: ChainRemoveCommand, detail: $"chain removed — {model.Chains.Count} left");
    }

    private CommandResult GoalHandler(CommandContext context, string[] args) =>
        ChainPointHandler(context: context, args: args, verb: "editor.sculpt.goal", isGoal: true);

    private CommandResult PoleHandler(CommandContext context, string[] args) =>
        ChainPointHandler(context: context, args: args, verb: "editor.sculpt.pole", isGoal: false);

    // The shared <idOrName> <x y z> [seat] handler for goal/pole moves.
    private CommandResult ChainPointHandler(CommandContext context, string[] args, string verb, bool isGoal) {
        if (args.Length is (< 4 or > 5)) {
            return Error(text: $"[{verb}: expected <idOrName> <x> <y> <z> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(args: args, at: 1, value: out var x) ||
            !EditorCommandModule.TryFloat(args: args, at: 2, value: out var y) ||
            !EditorCommandModule.TryFloat(args: args, at: 3, value: out var z)) {
            return Error(text: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 4, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var point = new Vector3(x: x, y: y, z: z);
        var applied = (isGoal
            ? model!.SetGoal(idOrName: args[0], goal: point)
            : model!.SetPole(idOrName: args[0], pole: point));

        if (!applied) {
            return Error(text: $"[{verb}: no chain '{args[0]}']");
        }

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"chain '{args[0]}' {(isGoal ? "goal" : "pole")}=({x:0.00}, {y:0.00}, {z:0.00}) — pose re-solved"
        ));
    }

    private static bool SeatToken(string token) =>
        (int.TryParse(s: token, provider: CultureInfo.InvariantCulture, result: out var value) && (value is >= 1 and <= PlayerRoster.MaxSlots));

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
