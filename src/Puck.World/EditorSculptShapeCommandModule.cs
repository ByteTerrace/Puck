using System.Globalization;
using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt SHAPE console surface — the assist-layer twins of the sculpt resting page's build chords plus
/// the typed-parameter setters a chord cannot express: add/duplicate/delete, the target cycle (which extends past
/// shapes into chain goals, folded onto <c>editor.sculpt.select</c>), primitive re-typing, uniform/per-axis scale
/// (folded grow/shrink steps included), and the field setter <c>editor.sculpt.set &lt;field&gt; &lt;value…&gt;</c>
/// (rotate/move/nudge/rename here, plus bend/dilate/onion/twist absorbed from the sibling style module — one door
/// for every typed-parameter field a chord cannot express, since none of those eight fields is itself bindable).
/// Positions are WORKBENCH-LOCAL coordinates — the bench origin is the frame. Everything here is client-local model
/// state; nothing crosses the wire until <c>editor.sculpt.commit</c>. A SEPARATE module to keep every class under its
/// analyzer ceilings.
/// </summary>
internal sealed class EditorSculptShapeCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    // The chord scale step: one press grows/shrinks the target ~15% — a deliberate act-scale step (held sweeps are
    // the stick's job, precision is editor.sculpt.scale's own <s>/<x y z> forms).
    private const float ScaleStepFactor = 1.15f;

    /// <summary>The add act (South on the sculpt resting page): the brush's primitive at the spawn point.</summary>
    public const string AddCommand = "editor.sculpt.add";
    /// <summary>The deselect act (West on the LT bench page): the target reverts to the brush.</summary>
    public const string DeselectCommand = "editor.sculpt.deselect";
    /// <summary>The duplicate act (D-pad Up on the sculpt resting page).</summary>
    public const string DuplicateCommand = "editor.sculpt.duplicate";
    /// <summary>The primitive-cycle act (North on the sculpt resting page).</summary>
    public const string PrimitiveCommand = "editor.sculpt.primitive";
    /// <summary>The delete act (D-pad Down on the sculpt resting page).</summary>
    public const string RemoveCommand = "editor.sculpt.remove";
    /// <summary>The scale verb: <c>editor.sculpt.scale &lt;s|x y z|grow|shrink&gt;</c>. D-pad Right/Left on the RT
    /// style page bind it with a constant Axis1D value (+1 grow, -1 shrink) in place of an argument.</summary>
    public const string ScaleCommand = "editor.sculpt.scale";
    /// <summary>The select verb, widened to fold the target cycle onto itself: <c>editor.sculpt.select
    /// &lt;id|name|next|prev&gt;</c>. D-pad Right/Left on the sculpt resting page bind it with a constant Axis1D
    /// value (+1 next, -1 prev) in place of an argument. The cycle extends past shapes into chain goals.</summary>
    public const string SelectCommand = "editor.sculpt.select";

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;

    /// <summary>Parses a primitive name token (case-insensitive). Internal — shared with the add/primitive verbs.</summary>
    /// <param name="token">The name token.</param>
    /// <param name="type">The parsed primitive.</param>
    internal static bool TryParsePrimitive(ReadOnlySpan<char> token, out AvatarPrimitive type) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "sphere"
        )) {
            type = AvatarPrimitive.Sphere;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "box"
        )) {
            type = AvatarPrimitive.Box;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "torus"
        )) {
            type = AvatarPrimitive.Torus;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "cylinder"
        )) {
            type = AvatarPrimitive.Cylinder;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "capsule"
        )) {
            type = AvatarPrimitive.Capsule;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "ellipsoid"
        )) {
            type = AvatarPrimitive.Ellipsoid;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "roundcone"
        )) {
            type = AvatarPrimitive.RoundCone;

            return true;
        }

        type = AvatarPrimitive.Sphere;

        return false;
    }

    private CommandResult AddHandler(CommandContext context, WireArgs args) {
        // Shapes: [primitive] [x y z] [seat] — the primitive token is non-numeric, so presence is unambiguous.
        var hasType = ((args.Count >= 1) && TryParsePrimitive(
            token: args[0],
            type: out _
        ));
        var positionAt = (hasType
            ? 1
            : 0
        );
        var hasPosition = ((args.Count >= (positionAt + 3)) &&
            EditorCommandModule.TryFloat(
            args: in args,
            at: positionAt,
            value: out _
        ));

        var x = 0f;
        var y = 0f;
        var z = 0f;

        if (
            hasPosition &&
            (!EditorCommandModule.TryFloat(
            args: in args,
            at: positionAt,
            value: out x
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: (positionAt + 1),
            value: out y
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: (positionAt + 2),
            value: out z
        ))
        ) {
            return CommandResult.Error(output: $"[{AddCommand}: could not parse <x> <y> <z> as finite numbers]");
        }

        var seatAt = (positionAt + (hasPosition
            ? 3
            : 0));

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: seatAt,
            context: context,
            session: m_session,
            verb: AddCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        AvatarPrimitive? type = null;

        if (
            hasType &&
            TryParsePrimitive(
            token: args[0],
            type: out var parsedType
        )
        ) {
            type = parsedType;
        } else if (
            (args.Count > 0) &&
            !hasType &&
            (args.Count > seatAt) &&
            !hasPosition &&
            !int.TryParse(
            s: args[0],
            result: out _
        )
        ) {
            return CommandResult.Error(output: $"[{AddCommand}: unknown primitive '{args[0].ToString()}' — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone]");
        }

        var added = model!.AddShape(
            type: type,
            position: (hasPosition
            ? new Vector3(
                    x: x,
                    y: y,
                    z: z
                )
            : null)
        );

        if (added is not { } shape) {
            return CommandResult.Error(output: $"[{AddCommand}: shape budget spent ({model.StampShapeCount}/{model.ShapeCapacity}) — remove a shape first]");
        }

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: AddCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00}) selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
            )
        );
    }
    // Shared target-cycle body — through the shapes, then the chain goals, wrapping through none/brush.
    private CommandResult CycleCore(int slot, SculptModel model, int direction) {
        model.CycleSelection(direction: direction);

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: SelectCommand,
            detail: DescribeTarget(model: model)
        );
    }
    // The target readout the cycle/position echoes share.
    private static string DescribeTarget(SculptModel model) {
        if (model.TargetIsGoal) {
            var chain = model.TargetGoalChain!;

            return $"target=goal chain {chain.Id}{((chain.Name is { Length: > 0 } name)
                ? $" '{name}'"
                : string.Empty)}";
        }

        if (model.SelectedShape is { } shape) {
            return $"target=shape {shape.Id} ({shape.Type})";
        }

        return "target=brush";
    }
    private CommandResult DeselectHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: DeselectCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        model!.Deselect();

        return EditorSculptCommandModule.Echo(
            detail: "target=brush",
            slot: slot,
            verb: DeselectCommand
        );
    }
    private CommandResult DuplicateHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: DuplicateCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DuplicateTarget()) {
            return CommandResult.Error(output: $"[{DuplicateCommand}: needs a selected shape and free budget ({model.StampShapeCount}/{model.ShapeCapacity})]");
        }

        var twin = model.SelectedShape!.Value;

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: DuplicateCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"twin shape {twin.Id} selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
            )
        );
    }
    private CommandResult KnobSetHandler(CommandContext context, in WireArgs args, string field, Func<SculptModel, float, float> apply) {
        var verb = $"editor.sculpt.set {field}";

        if (args.Count is (< 2 or > 3)) {
            return CommandResult.Error(output: $"[{verb}: expected <v> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(
            args: in args,
            at: 1,
            value: out var value
        )) {
            return CommandResult.Error(output: $"[{verb}: could not parse <v> as a finite number]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 2,
            context: context,
            session: m_session,
            verb: verb,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var applied = apply(
            model!,
            value
        );

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: verb,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{applied:0.00}"
            )
        );
    }
    // CommandContext.Origin is the discriminator, not Value.Kind — this verb declares Axis1D, so the text path's
    // own impulse value would read Axis1D too. See CommandDefinition.WithWireArgs's doctrine comment.
    private CommandResult NoArgSelectHandler(CommandContext context) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            context: context,
            args: WireArgs.Empty,
            at: 0,
            verb: SelectCommand,
            session: m_session,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: "[editor.sculpt.select: expected <id|name>, next, or prev]");
        }

        return CycleCore(
            slot: slot,
            model: model!,
            direction: ((context.Value.AsAxis1D >= 0f)
            ? 1
            : -1)
        );
    }
    private CommandResult PositionSetHandler(CommandContext context, in WireArgs args, bool relative) {
        var verb = $"editor.sculpt.set {(relative
            ? "nudge"
            : "move")}";

        if (args.Count is (< 4 or > 5)) {
            return CommandResult.Error(output: $"[{verb}: expected <x> <y> <z> plus an optional seat 1..4]");
        }

        if (
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 1,
            value: out var x
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 2,
            value: out var y
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 3,
            value: out var z
        )
        ) {
            return CommandResult.Error(output: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 4,
            context: context,
            session: m_session,
            verb: verb,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var requested = new Vector3(
            x: x,
            y: y,
            z: z
        );

        if (relative) {
            if (model!.TargetPosition is not { } current) {
                return CommandResult.Error(output: $"[{verb}: no target — select a shape or a chain goal first]");
            }

            requested = (current + requested);
        }

        if (model!.SetTargetPosition(position: requested) is not { } applied) {
            return CommandResult.Error(output: $"[{verb}: no target — select a shape or a chain goal first]");
        }

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: verb,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{DescribeTarget(model: model)} at ({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
            )
        );
    }
    private CommandResult PrimitiveHandler(CommandContext context, WireArgs args) {
        // Shapes: [] = cycle next (the chord), [next|prev|name] [seat].
        var hasToken = ((args.Count >= 1) && !int.TryParse(
            s: args[0],
            result: out _
        ));

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: (hasToken
            ? 1
            : 0),
            context: context,
            session: m_session,
            verb: PrimitiveCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        AvatarPrimitive applied;

        if (
            !hasToken ||
            args.Is(
            index: 0,
            value: "next"
        )
        ) {
            applied = model!.CyclePrimitive(direction: 1);
        } else if (args.Is(
            index: 0,
            value: "prev"
        )) {
            applied = model!.CyclePrimitive(direction: -1);
        } else if (TryParsePrimitive(
            token: args[0],
            type: out var parsed
        )) {
            model!.SetPrimitive(type: parsed);
            applied = parsed;
        } else {
            return CommandResult.Error(output: $"[{PrimitiveCommand}: unknown primitive '{args[0].ToString()}' — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone|next|prev]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"{applied} ({(model!.TargetIsBrush
            ? "brush — the next add"
            : "selected shape")})",
            slot: slot,
            verb: PrimitiveCommand
        );
    }
    private CommandResult RemoveHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: RemoveCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.DeleteSelected()) {
            return CommandResult.Error(output: $"[{RemoveCommand}: no shape selected — editor.sculpt.select or the target cycle first]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"shape removed — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes",
            slot: slot,
            verb: RemoveCommand
        );
    }
    private CommandResult RenameSetHandler(CommandContext context, in WireArgs args) {
        const string Verb = "editor.sculpt.set rename";

        if (args.Count is (< 2 or > 3)) {
            return CommandResult.Error(output: $"[{Verb}: expected <name> plus an optional seat 1..4]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 2,
            context: context,
            session: m_session,
            verb: Verb,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var name = args[1].ToString();

        if (!model!.RenameSelected(name: name)) {
            return CommandResult.Error(output: $"[{Verb}: no shape selected]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"shape named '{name}'",
            slot: slot,
            verb: Verb
        );
    }
    private CommandResult RotateSetHandler(CommandContext context, in WireArgs args) {
        const string Verb = "editor.sculpt.set rotate";

        if (args.Count is (< 4 or > 5)) {
            return CommandResult.Error(output: $"[{Verb}: expected <yawDeg> <pitchDeg> <rollDeg> plus an optional seat 1..4]");
        }

        if (
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 1,
            value: out var yaw
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 2,
            value: out var pitch
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 3,
            value: out var roll
        )
        ) {
            return CommandResult.Error(output: $"[{Verb}: could not parse the angles as finite numbers]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 4,
            context: context,
            session: m_session,
            verb: Verb,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        if (!model!.SetTargetRotation(
            pitchDegrees: pitch,
            rollDegrees: roll,
            yawDegrees: yaw
        )) {
            return CommandResult.Error(output: $"[{Verb}: no shape selected — a chain goal has no orientation]");
        }

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: Verb,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"yaw={yaw:0.#}° pitch={pitch:0.#}° roll={roll:0.#}°"
            )
        );
    }
    // The scale fold: a leading grow|shrink literal, or (with no token) a bound constant Axis1D value from the
    // style page's D-pad Right/Left step chord — see EditorCommandModule.TryDirection. Anything else falls through
    // to the original <s> / <x y z> forms.
    private CommandResult ScaleHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "shrink",
            positive: "grow"
        )) {
            var (stepSlot, stepModel, stepError) = EditorSculptCommandModule.ResolveBench(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: "editor.sculpt.scale",
                session: m_session,
                workbench: m_workbench
            );

            if (stepError is { } resolveStepError) {
                return resolveStepError;
            }

            var factor = ((direction > 0)
                ? ScaleStepFactor
                : (1f / ScaleStepFactor)
            );
            var stepped = stepModel!.SetTargetScale(scale: (stepModel.TargetScale * factor));

            return EditorSculptCommandModule.Echo(
                slot: stepSlot,
                verb: "editor.sculpt.scale",
                detail: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"scale=({stepped.X:0.00}, {stepped.Y:0.00}, {stepped.Z:0.00})"
                )
            );
        }

        // Shapes: <s> [seat] or <x y z> [seat].
        if (args.Count is (< 1 or > 4)) {
            return CommandResult.Error(output: "[editor.sculpt.scale: expected <s>, <x> <y> <z>, grow, or shrink, plus an optional seat 1..4]");
        }

        var perAxis = ((args.Count >= 3) && EditorCommandModule.TryFloat(
            args: in args,
            at: 2,
            value: out _
        ));
        var seatAt = (perAxis
            ? 3
            : 1
        );

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: seatAt,
            context: context,
            session: m_session,
            verb: "editor.sculpt.scale",
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        Vector3 requested;

        if (perAxis) {
            if (
                !EditorCommandModule.TryFloat(
                args: in args,
                at: 0,
                value: out var x
            ) ||
                !EditorCommandModule.TryFloat(
                args: in args,
                at: 1,
                value: out var y
            ) ||
                !EditorCommandModule.TryFloat(
                args: in args,
                at: 2,
                value: out var z
            )
            ) {
                return CommandResult.Error(output: "[editor.sculpt.scale: could not parse <x> <y> <z> as finite numbers]");
            }

            requested = new Vector3(
                x: x,
                y: y,
                z: z
            );
        } else {
            if (!EditorCommandModule.TryFloat(
                args: in args,
                at: 0,
                value: out var uniform
            )) {
                return CommandResult.Error(output: "[editor.sculpt.scale: could not parse <s> as a finite number]");
            }

            requested = new Vector3(value: uniform);
        }

        var applied = model!.SetTargetScale(scale: requested);

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: "editor.sculpt.scale",
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"scale=({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
            )
        );
    }
    // The select fold: no args -> bound dispatch (direction from a constant Axis1D value; a Digital value has no
    // default here, unlike editor.camera's toggle or editor.select's deselect, so it refuses); a leading next|prev
    // literal -> the same cycle, typed; otherwise the original <id|name> [seat] explicit form. See
    // EditorCommandModule.TryDirection's doctrine comment for the bound-constant mechanism.
    private CommandResult SelectHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return NoArgSelectHandler(context: context);
        }

        if (
            args.Is(
            index: 0,
            value: "next"
        ) ||
            args.Is(
            index: 0,
            value: "prev"
        )
        ) {
            var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
                args: in args,
                at: 1,
                context: context,
                session: m_session,
                verb: SelectCommand,
                workbench: m_workbench
            );

            if (error is { } benchError) {
                return benchError;
            }

            return CycleCore(
                slot: slot,
                model: model!,
                direction: (args.Is(
                    index: 0,
                    value: "next"
                )
                ? 1
                : -1)
            );
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.select: expected <id|name>, next, prev, plus an optional seat 1..4]");
        }

        var (idSlot, idModel, idError) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 1,
            context: context,
            session: m_session,
            verb: SelectCommand,
            workbench: m_workbench
        );

        if (idError is { } resolveError) {
            return resolveError;
        }

        var idOrName = args[0].ToString();

        if (idModel!.Select(idOrName: idOrName) is not { } shape) {
            return CommandResult.Error(output: $"[editor.sculpt.select: no shape '{idOrName}' — editor.sculpt.status lists the model]");
        }

        return EditorSculptCommandModule.Echo(
            slot: idSlot,
            verb: SelectCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00})"
            )
        );
    }
    // The one door for every field editor.sculpt.set absorbed: bend/dilate/onion/twist (from the sibling style
    // module — plain SculptModel setters, no cross-module call needed), rotate/move/nudge/rename (already local).
    // Every sub-form's argument indices are shifted by 1 relative to their old standalone verbs (args[0] is now the
    // field name).
    private CommandResult SetHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return CommandResult.Error(output: "[editor.sculpt.set: expected <field> <value…> — bend|dilate|onion|twist|rotate|move|nudge|rename]");
        }

        if (args.Is(
            index: 0,
            value: "bend"
        )) {
            return KnobSetHandler(
                context: context,
                args: in args,
                field: "bend",
                apply: static (model, value) => model.SetBend(value: value)
            );
        }

        if (args.Is(
            index: 0,
            value: "dilate"
        )) {
            return KnobSetHandler(
                context: context,
                args: in args,
                field: "dilate",
                apply: static (model, value) => model.SetDilate(value: value)
            );
        }

        if (args.Is(
            index: 0,
            value: "onion"
        )) {
            return KnobSetHandler(
                context: context,
                args: in args,
                field: "onion",
                apply: static (model, value) => model.SetOnion(value: value)
            );
        }

        if (args.Is(
            index: 0,
            value: "twist"
        )) {
            return KnobSetHandler(
                context: context,
                args: in args,
                field: "twist",
                apply: static (model, value) => model.SetTwist(value: value)
            );
        }

        if (args.Is(
            index: 0,
            value: "rotate"
        )) {
            return RotateSetHandler(
                args: in args,
                context: context
            );
        }

        if (args.Is(
            index: 0,
            value: "move"
        )) {
            return PositionSetHandler(
                args: in args,
                context: context,
                relative: false
            );
        }

        if (args.Is(
            index: 0,
            value: "nudge"
        )) {
            return PositionSetHandler(
                args: in args,
                context: context,
                relative: true
            );
        }

        if (args.Is(
            index: 0,
            value: "rename"
        )) {
            return RenameSetHandler(
                args: in args,
                context: context
            );
        }

        return CommandResult.Error(output: $"[editor.sculpt.set: unknown field '{args[0].ToString()}' — bend|dilate|onion|twist|rotate|move|nudge|rename]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: AddCommand,
            description: "Adds a shape to the seat's sculpt and selects it: editor.sculpt.add [primitive] [<x> <y> <z>] [seat] — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone (default: the brush's primitive) at workbench-local coordinates (default: the spawn point). The new shape inherits the brush's style and the brush's palette slot advances (siblings stay distinct). The chord twin is South on the sculpt page.",
            handler: AddHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: RemoveCommand,
            description: "Deletes the SELECTED shape (the selection clears): editor.sculpt.remove [seat]. The chord twin is D-pad Down on the sculpt page.",
            handler: RemoveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: DuplicateCommand,
            description: "Duplicates the SELECTED shape in place (nudged aside; a grouped member's twin joins the same group) and selects the twin: editor.sculpt.duplicate [seat]. The chord twin is D-pad Up on the sculpt page.",
            handler: DuplicateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SelectCommand,
            description: "Selects a sculpt shape by id or name (editor.sculpt.select <id|name> [seat]), OR folds the target cycle onto the same verb: editor.sculpt.select [next|prev] [seat] — forward/backward through the shapes, THEN the chain goals, wrapping through none/brush. 'next'/'prev' are the D-pad Right/Left chord twins on the sculpt resting page, bound with a constant value in place of the argument. Edit verbs then act on the target; editor.sculpt.deselect reverts it to the brush.",
            handler: SelectHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: DeselectCommand,
            description: "Clears the sculpt selection (the target reverts to the brush): editor.sculpt.deselect [seat]. The chord twin is West on the LT bench page.",
            handler: DeselectHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: PrimitiveCommand,
            description: "Re-types the TARGET's primitive: editor.sculpt.primitive [sphere|box|torus|cylinder|capsule|ellipsoid|roundcone|next|prev] [seat] (default next — the chord twin is North on the sculpt page). On the brush it changes what the next add draws.",
            handler: PrimitiveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ScaleCommand,
            description: "Sets or steps the TARGET's scale (uniform or per-axis, clamped 0.2..3): editor.sculpt.scale <s> [seat], editor.sculpt.scale <x> <y> <z> [seat], or editor.sculpt.scale <grow|shrink> [seat] (±~15% — the typed twins of the style page's Grow/Shrink D-pad chord, which binds this same verb with no argument, direction from the binding's constant value).",
            handler: ScaleHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.set",
            description: "Sets a field by name on the sculpt TARGET or brush — the one door for the typed-parameter fields no chord expresses: editor.sculpt.set <field> <value…> [seat] — bend <v> (about local Y, clamped ±1.5), dilate <v> (inflation radius, clamped 0..0.2), onion <v> (shell thickness, clamped 0..0.2), twist <v> (about local Y, clamped ±3), rotate <yawDeg> <pitchDeg> <rollDeg> (Tait-Bryan degrees — SELECTED shape only, a chain goal has no orientation), move <x> <y> <z> (ABSOLUTE workbench-local coordinates — a targeted chain goal moves and re-solves), nudge <dx> <dy> <dz> (RELATIVE workbench-local delta), rename <name> (SELECTED shape only).",
            handler: SetHandler
        );
    }

}
