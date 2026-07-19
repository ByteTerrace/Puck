using System.Globalization;
using System.Numerics;
using Puck.Authoring;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt SHAPE console surface — the assist-layer twins of the sculpt resting page's build chords plus
/// the typed-parameter setters a chord cannot express: add/duplicate/delete, the target cycle (which extends past
/// shapes into chain goals), primitive re-typing, and exact transform placement (positions are WORKBENCH-LOCAL
/// coordinates — the bench origin is the frame). Everything here is client-local model state; nothing crosses the
/// wire until <c>editor.sculpt.commit</c>. A SEPARATE module to keep every class under its analyzer ceilings.
/// </summary>
internal sealed class EditorSculptShapeCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    /// <summary>The add act (South on the sculpt resting page): the brush's primitive at the spawn point.</summary>
    public const string AddCommand = "editor.sculpt.add";
    /// <summary>The delete act (D-pad Down on the sculpt resting page).</summary>
    public const string RemoveCommand = "editor.sculpt.remove";
    /// <summary>The duplicate act (D-pad Up on the sculpt resting page).</summary>
    public const string DuplicateCommand = "editor.sculpt.duplicate";
    /// <summary>The target-cycle-next act (D-pad Right on the sculpt resting page; cycles shapes THEN chain goals).</summary>
    public const string NextCommand = "editor.sculpt.next";
    /// <summary>The target-cycle-previous act (D-pad Left on the sculpt resting page).</summary>
    public const string PrevCommand = "editor.sculpt.prev";
    /// <summary>The deselect act (West on the LT bench page): the target reverts to the brush.</summary>
    public const string DeselectCommand = "editor.sculpt.deselect";
    /// <summary>The primitive-cycle act (North on the sculpt resting page).</summary>
    public const string PrimitiveCommand = "editor.sculpt.primitive";
    /// <summary>The uniform grow step (D-pad Right on the RT style page).</summary>
    public const string GrowCommand = "editor.sculpt.grow";
    /// <summary>The uniform shrink step (D-pad Left on the RT style page).</summary>
    public const string ShrinkCommand = "editor.sculpt.shrink";

    // The chord scale step: one press grows/shrinks the target ~15% — a deliberate act-scale step (held sweeps are
    // the stick's job, precision is editor.sculpt.scale's).
    private const float ScaleStepFactor = 1.15f;

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: AddCommand,
            description: "Adds a shape to the seat's sculpt and selects it: editor.sculpt.add [primitive] [<x> <y> <z>] [seat] — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone (default: the brush's primitive) at workbench-local coordinates (default: the spawn point). The new shape inherits the brush's style and the brush's palette slot advances (siblings stay distinct). The chord twin is South on the sculpt page.",
            handler: AddHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: RemoveCommand,
            description: "Deletes the SELECTED shape (the selection clears): editor.sculpt.remove [seat]. The chord twin is D-pad Down on the sculpt page.",
            handler: RemoveHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: DuplicateCommand,
            description: "Duplicates the SELECTED shape in place (nudged aside; a grouped member's twin joins the same group) and selects the twin: editor.sculpt.duplicate [seat]. The chord twin is D-pad Up on the sculpt page.",
            handler: DuplicateHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.select",
            description: "Selects a sculpt shape by id or name: editor.sculpt.select <id|name> [seat]. Edit verbs then act on it; editor.sculpt.deselect reverts the target to the brush.",
            handler: SelectHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: NextCommand,
            description: "Cycles the sculpt target forward — through the shapes, THEN the chain goals, wrapping through none/brush: editor.sculpt.next [seat]. The chord twin is D-pad Right on the sculpt page.",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: 1, verb: NextCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PrevCommand,
            description: "Cycles the sculpt target backward: editor.sculpt.prev [seat]. The chord twin is D-pad Left on the sculpt page.",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: -1, verb: PrevCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: DeselectCommand,
            description: "Clears the sculpt selection (the target reverts to the brush): editor.sculpt.deselect [seat]. The chord twin is West on the LT bench page.",
            handler: DeselectHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PrimitiveCommand,
            description: "Re-types the TARGET's primitive: editor.sculpt.primitive [sphere|box|torus|cylinder|capsule|ellipsoid|roundcone|next|prev] [seat] (default next — the chord twin is North on the sculpt page). On the brush it changes what the next add draws.",
            handler: PrimitiveHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.move",
            description: "Places the TARGET at exact workbench-local coordinates (a targeted chain goal moves and re-solves): editor.sculpt.move <x> <y> <z> [seat]. The stick twin is the move stick while sculpting.",
            handler: (context, args) => PositionHandler(context: context, args: args, relative: false, verb: "editor.sculpt.move")
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.nudge",
            description: "Moves the TARGET by a workbench-local delta: editor.sculpt.nudge <dx> <dy> <dz> [seat].",
            handler: (context, args) => PositionHandler(context: context, args: args, relative: true, verb: "editor.sculpt.nudge")
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.rotate",
            description: "Sets the SELECTED shape's orientation from Tait-Bryan degrees (yaw about +Y, pitch about +X, roll about +Z): editor.sculpt.rotate <yawDeg> <pitchDeg> <rollDeg> [seat].",
            handler: RotateHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.scale",
            description: "Sets the TARGET's scale (uniform or per-axis, clamped 0.2..3): editor.sculpt.scale <s> [seat] or editor.sculpt.scale <x> <y> <z> [seat]. The chord twins are the style page's Grow/Shrink steps.",
            handler: ScaleHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: GrowCommand,
            description: "Steps the TARGET's uniform scale up ~15%: editor.sculpt.grow [seat]. The chord twin is D-pad Right on the RT style page.",
            handler: (context, args) => ScaleStepHandler(context: context, args: args, grow: true, verb: GrowCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ShrinkCommand,
            description: "Steps the TARGET's uniform scale down ~15%: editor.sculpt.shrink [seat]. The chord twin is D-pad Left on the RT style page.",
            handler: (context, args) => ScaleStepHandler(context: context, args: args, grow: false, verb: ShrinkCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.rename",
            description: "Names the SELECTED shape (chain definitions and selection accept names): editor.sculpt.rename <name> [seat].",
            handler: RenameHandler
        );
    }

    /// <summary>Parses a primitive name token (case-insensitive). Internal — shared with the add/primitive verbs.</summary>
    /// <param name="token">The name token.</param>
    /// <param name="type">The parsed primitive.</param>
    internal static bool TryParsePrimitive(string token, out AvatarPrimitive type) {
        switch (token.ToLowerInvariant()) {
            case "sphere": type = AvatarPrimitive.Sphere; return true;
            case "box": type = AvatarPrimitive.Box; return true;
            case "torus": type = AvatarPrimitive.Torus; return true;
            case "cylinder": type = AvatarPrimitive.Cylinder; return true;
            case "capsule": type = AvatarPrimitive.Capsule; return true;
            case "ellipsoid": type = AvatarPrimitive.Ellipsoid; return true;
            case "roundcone": type = AvatarPrimitive.RoundCone; return true;
            default: type = AvatarPrimitive.Sphere; return false;
        }
    }

    private CommandResult AddHandler(CommandContext context, string[] args) {
        // Shapes: [primitive] [x y z] [seat] — the primitive token is non-numeric, so presence is unambiguous.
        var hasType = ((args.Length >= 1) && TryParsePrimitive(token: args[0], type: out _));
        var positionAt = (hasType ? 1 : 0);
        var hasPosition = (args.Length >= (positionAt + 3)) &&
            EditorCommandModule.TryFloat(args: args, at: positionAt, value: out _);

        var x = 0f;
        var y = 0f;
        var z = 0f;

        if (hasPosition && (!EditorCommandModule.TryFloat(args: args, at: positionAt, value: out x) ||
            !EditorCommandModule.TryFloat(args: args, at: (positionAt + 1), value: out y) ||
            !EditorCommandModule.TryFloat(args: args, at: (positionAt + 2), value: out z))) {
            return Error(text: $"[{AddCommand}: could not parse <x> <y> <z> as finite numbers]");
        }

        var seatAt = (positionAt + (hasPosition ? 3 : 0));
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: seatAt, verb: AddCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        AvatarPrimitive? type = null;

        if (hasType && TryParsePrimitive(token: args[0], type: out var parsedType)) {
            type = parsedType;
        } else if ((args.Length > 0) && !hasType && (args.Length > seatAt) && !hasPosition && !int.TryParse(s: args[0], result: out _)) {
            return Error(text: $"[{AddCommand}: unknown primitive '{args[0]}' — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone]");
        }

        var added = model!.AddShape(type: type, position: (hasPosition ? new Vector3(x: x, y: y, z: z) : null));

        if (added is not { } shape) {
            return Error(text: $"[{AddCommand}: shape budget spent ({model.StampShapeCount}/{model.ShapeCapacity}) — remove a shape first]");
        }

        return Echo(slot: slot, verb: AddCommand, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00}) selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
        ));
    }

    private CommandResult RemoveHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: RemoveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DeleteSelected()) {
            return Error(text: $"[{RemoveCommand}: no shape selected — editor.sculpt.select or the target cycle first]");
        }

        return Echo(slot: slot, verb: RemoveCommand, detail: $"shape removed — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes");
    }

    private CommandResult DuplicateHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: DuplicateCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DuplicateTarget()) {
            return Error(text: $"[{DuplicateCommand}: needs a selected shape and free budget ({model.StampShapeCount}/{model.ShapeCapacity})]");
        }

        var twin = model.SelectedShape!.Value;

        return Echo(slot: slot, verb: DuplicateCommand, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"twin shape {twin.Id} selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
        ));
    }

    private CommandResult SelectHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.select: expected <id|name> plus an optional seat 1..4]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 1, verb: "editor.sculpt.select", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (model!.Select(idOrName: args[0]) is not { } shape) {
            return Error(text: $"[editor.sculpt.select: no shape '{args[0]}' — editor.sculpt.status lists the model]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.select", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00})"
        ));
    }

    private CommandResult CycleHandler(CommandContext context, string[] args, int direction, string verb) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        model!.CycleSelection(direction: direction);

        return Echo(slot: slot, verb: verb, detail: DescribeTarget(model: model));
    }

    private CommandResult DeselectHandler(CommandContext context, string[] args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: DeselectCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        model!.Deselect();

        return Echo(slot: slot, verb: DeselectCommand, detail: "target=brush");
    }

    private CommandResult PrimitiveHandler(CommandContext context, string[] args) {
        // Shapes: [] = cycle next (the chord), [next|prev|name] [seat].
        var hasToken = ((args.Length >= 1) && !int.TryParse(s: args[0], result: out _));
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: (hasToken ? 1 : 0), verb: PrimitiveCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        AvatarPrimitive applied;

        if (!hasToken || string.Equals(a: args[0], b: "next", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            applied = model!.CyclePrimitive(direction: 1);
        } else if (string.Equals(a: args[0], b: "prev", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            applied = model!.CyclePrimitive(direction: -1);
        } else if (TryParsePrimitive(token: args[0], type: out var parsed)) {
            model!.SetPrimitive(type: parsed);
            applied = parsed;
        } else {
            return Error(text: $"[{PrimitiveCommand}: unknown primitive '{args[0]}' — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone|next|prev]");
        }

        return Echo(slot: slot, verb: PrimitiveCommand, detail: $"{applied} ({(model!.TargetIsBrush ? "brush — the next add" : "selected shape")})");
    }

    private CommandResult PositionHandler(CommandContext context, string[] args, bool relative, string verb) {
        if (args.Length is (< 3 or > 4)) {
            return Error(text: $"[{verb}: expected <x> <y> <z> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(args: args, at: 0, value: out var x) ||
            !EditorCommandModule.TryFloat(args: args, at: 1, value: out var y) ||
            !EditorCommandModule.TryFloat(args: args, at: 2, value: out var z)) {
            return Error(text: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 3, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var requested = new Vector3(x: x, y: y, z: z);

        if (relative) {
            if (model!.TargetPosition is not { } current) {
                return Error(text: $"[{verb}: no target — select a shape or a chain goal first]");
            }

            requested = (current + requested);
        }

        if (model!.SetTargetPosition(position: requested) is not { } applied) {
            return Error(text: $"[{verb}: no target — select a shape or a chain goal first]");
        }

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{DescribeTarget(model: model)} at ({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
        ));
    }

    private CommandResult RotateHandler(CommandContext context, string[] args) {
        if (args.Length is (< 3 or > 4)) {
            return Error(text: "[editor.sculpt.rotate: expected <yawDeg> <pitchDeg> <rollDeg> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(args: args, at: 0, value: out var yaw) ||
            !EditorCommandModule.TryFloat(args: args, at: 1, value: out var pitch) ||
            !EditorCommandModule.TryFloat(args: args, at: 2, value: out var roll)) {
            return Error(text: "[editor.sculpt.rotate: could not parse the angles as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 3, verb: "editor.sculpt.rotate", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.SetTargetRotation(yawDegrees: yaw, pitchDegrees: pitch, rollDegrees: roll)) {
            return Error(text: "[editor.sculpt.rotate: no shape selected — a chain goal has no orientation]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.rotate", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"yaw={yaw:0.#}° pitch={pitch:0.#}° roll={roll:0.#}°"
        ));
    }

    private CommandResult ScaleHandler(CommandContext context, string[] args) {
        // Shapes: <s> [seat] or <x y z> [seat].
        if (args.Length is (< 1 or > 4)) {
            return Error(text: "[editor.sculpt.scale: expected <s> or <x> <y> <z>, plus an optional seat 1..4]");
        }

        var perAxis = (args.Length >= 3) && EditorCommandModule.TryFloat(args: args, at: 2, value: out _);
        var seatAt = (perAxis ? 3 : 1);
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: seatAt, verb: "editor.sculpt.scale", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        Vector3 requested;

        if (perAxis) {
            if (!EditorCommandModule.TryFloat(args: args, at: 0, value: out var x) ||
                !EditorCommandModule.TryFloat(args: args, at: 1, value: out var y) ||
                !EditorCommandModule.TryFloat(args: args, at: 2, value: out var z)) {
                return Error(text: "[editor.sculpt.scale: could not parse <x> <y> <z> as finite numbers]");
            }

            requested = new Vector3(x: x, y: y, z: z);
        } else {
            if (!EditorCommandModule.TryFloat(args: args, at: 0, value: out var uniform)) {
                return Error(text: "[editor.sculpt.scale: could not parse <s> as a finite number]");
            }

            requested = new Vector3(value: uniform);
        }

        var applied = model!.SetTargetScale(scale: requested);

        return Echo(slot: slot, verb: "editor.sculpt.scale", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"scale=({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
        ));
    }

    private CommandResult ScaleStepHandler(CommandContext context, string[] args, bool grow, string verb) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var factor = (grow ? ScaleStepFactor : (1f / ScaleStepFactor));
        var applied = model!.SetTargetScale(scale: (model.TargetScale * factor));

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"scale=({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
        ));
    }

    private CommandResult RenameHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.rename: expected <name> plus an optional seat 1..4]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: args, at: 1, verb: "editor.sculpt.rename", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.RenameSelected(name: args[0])) {
            return Error(text: "[editor.sculpt.rename: no shape selected]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.rename", detail: $"shape named '{args[0]}'");
    }

    // The target readout the cycle/position echoes share.
    private static string DescribeTarget(SculptModel model) {
        if (model.TargetIsGoal) {
            var chain = model.TargetGoalChain!;

            return $"target=goal chain {chain.Id}{((chain.Name is { Length: > 0 } name) ? $" '{name}'" : string.Empty)}";
        }

        if (model.SelectedShape is { } shape) {
            return $"target=shape {shape.Id} ({shape.Type})";
        }

        return "target=brush";
    }

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
