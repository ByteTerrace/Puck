using System.Globalization;
using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Commands;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt STYLE console surface — the assist-layer twins of the RT style page's chords plus the numeric
/// setters: blend ops (with the group-of-one coercion), smooth radius (with its folded up/down step chord),
/// palette-slot assignment (with its folded next/prev cycle chord) and palette-entry editing, mirror, and group
/// link/ungroup. The twist/bend/dilate/onion field knobs moved to the sibling shape module's
/// <c>editor.sculpt.set &lt;field&gt; &lt;value…&gt;</c> — they were UNBINDABLE numeric setters, so folding them
/// under one door cost no chord. All client-local model state. A SEPARATE module to keep every class under its
/// analyzer ceilings.
/// </summary>
internal sealed class EditorSculptStyleCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    // The chord smooth step: one press moves the radius by a tenth of its envelope — act-scale, precision typed.
    private const float SmoothStep = 0.05f;

    /// <summary>The blend-cycle act (South on the RT style page).</summary>
    public const string BlendCommand = "editor.sculpt.blend";
    /// <summary>The material verb: <c>editor.sculpt.material &lt;slot|next|prev&gt;</c>. East/West on the RT style
    /// page bind it with a constant Axis1D value (+1 next, -1 prev) in place of an argument.</summary>
    public const string MaterialCommand = "editor.sculpt.material";
    /// <summary>The mirror-toggle act (North on the RT style page).</summary>
    public const string MirrorCommand = "editor.sculpt.mirror";
    /// <summary>The smooth verb: <c>editor.sculpt.smooth &lt;v|up|down&gt;</c>. D-pad Up/Down on the RT style page
    /// bind it with a constant Axis1D value (+1 up, -1 down) in place of an argument.</summary>
    public const string SmoothCommand = "editor.sculpt.smooth";

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;

    private CommandResult BlendHandler(CommandContext context, WireArgs args) {
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
            verb: BlendCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        SdfBlendOp applied;

        if (
            !hasToken ||
            args.Is(
            index: 0,
            value: "next"
        )
        ) {
            applied = model!.CycleBlend(direction: 1);
        } else if (args.Is(
            index: 0,
            value: "prev"
        )) {
            applied = model!.CycleBlend(direction: -1);
        } else if (TryParseBlend(
            token: args[0],
            blend: out var parsed
        )) {
            model!.SetBlend(blend: parsed);
            applied = parsed;
        } else {
            return CommandResult.Error(output: $"[{BlendCommand}: unknown op '{args[0].ToString()}' — union|smoothunion|subtract|smoothsubtract|intersect|smoothintersect|xor|next|prev]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"{applied}",
            slot: slot,
            verb: BlendCommand
        );
    }
    private CommandResult LinkHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: "editor.sculpt.link",
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        if (model!.LinkWithPrevious() is not { } groupId) {
            return CommandResult.Error(output: "[editor.sculpt.link: needs two distinct selections in a row (select A, select B, link)]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"group {groupId}",
            slot: slot,
            verb: "editor.sculpt.link"
        );
    }
    // The material fold: a leading next|prev literal, or (with no token) a bound constant Axis1D value from the
    // style page's Color+/- East/West chord — see EditorCommandModule.TryDirection. Anything else falls through to
    // the original <slot> form. Mirrors editor.sculpt.primitive's shape exactly.
    private CommandResult MaterialHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "prev",
            positive: "next"
        )) {
            var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: "editor.sculpt.material",
                session: m_session,
                workbench: m_workbench
            );

            if (error is { } benchError) {
                return benchError;
            }

            return EditorSculptCommandModule.Echo(
                slot: slot,
                verb: "editor.sculpt.material",
                detail: $"slot {model!.CycleMaterial(direction: direction)}"
            );
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.material: expected <slot 0..15>, next, or prev, plus an optional seat 1..4]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var requested
        )) {
            return CommandResult.Error(output: "[editor.sculpt.material: could not parse <slot> as an integer]");
        }

        var (slotAt, slotModel, slotError) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 1,
            context: context,
            session: m_session,
            verb: "editor.sculpt.material",
            workbench: m_workbench
        );

        if (slotError is { } resolveError) {
            return resolveError;
        }

        return EditorSculptCommandModule.Echo(
            slot: slotAt,
            verb: "editor.sculpt.material",
            detail: $"slot {slotModel!.SetMaterialIndex(index: requested)}"
        );
    }
    private CommandResult MirrorHandler(CommandContext context, WireArgs args) {
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
            verb: MirrorCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        bool applied;

        if (
            hasToken &&
            args.Is(
            index: 0,
            value: "on"
        )
        ) {
            applied = (model!.TargetMirror || model.ToggleMirror());
        } else if (
            hasToken &&
            args.Is(
            index: 0,
            value: "off"
        )
        ) {
            applied = (model!.TargetMirror && model.ToggleMirror());
        } else if (hasToken) {
            return CommandResult.Error(output: $"[{MirrorCommand}: expected on, off, or nothing (toggle)]");
        } else {
            applied = model!.ToggleMirror();
        }

        return EditorSculptCommandModule.Echo(
            detail: $"mirror {(applied
            ? "on"
            : "off")}",
            slot: slot,
            verb: MirrorCommand
        );
    }
    private CommandResult PaletteHandler(CommandContext context, WireArgs args) {
        // Shapes: <slot> <r> <g> <b> [emissive [specular [shininess]]] — acts on the invoking seat's bench (the
        // variable float run makes a trailing seat token ambiguous, so this verb deliberately takes none).
        if (args.Count is (< 4 or > 7)) {
            return CommandResult.Error(output: "[editor.sculpt.palette: expected <slot> <r> <g> <b> [emissive [specular [shininess]]]]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var paletteSlot
        )) {
            return CommandResult.Error(output: "[editor.sculpt.palette: could not parse <slot> as an integer]");
        }

        if (
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 1,
            value: out var r
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 2,
            value: out var g
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 3,
            value: out var b
        )
        ) {
            return CommandResult.Error(output: "[editor.sculpt.palette: could not parse <r> <g> <b> as finite numbers]");
        }

        var extras = new float[3];
        var extraCount = 0;

        for (var at = 4; (at < args.Count); at++) {
            if (!EditorCommandModule.TryFloat(
                args: in args,
                at: at,
                value: out extras[extraCount]
            )) {
                return CommandResult.Error(output: $"[editor.sculpt.palette: could not parse '{args[at].ToString()}' as a finite number]");
            }

            extraCount++;
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            context: context,
            args: WireArgs.Empty,
            at: 0,
            verb: "editor.sculpt.palette",
            session: m_session,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var material = new SdfMaterial(Albedo: new Vector3(
            x: Math.Clamp(
                max: 1f,
                min: 0f,
                value: r
            ),
            y: Math.Clamp(
                max: 1f,
                min: 0f,
                value: g
            ),
            z: Math.Clamp(
                max: 1f,
                min: 0f,
                value: b
            )
        ));

        if (extraCount >= 1) {
            material = (material with {
                Emissive = MathF.Max(
                x: extras[0],
                y: 0f
            ),
            });
        }

        if (extraCount >= 2) {
            material = (material with {
                Specular = MathF.Max(
                x: extras[1],
                y: 0f
            ),
            });
        }

        if (extraCount >= 3) {
            material = (material with {
                Shininess = MathF.Max(
                x: extras[2],
                y: 1f
            ),
            });
        }

        model!.SetPaletteEntry(
            index: paletteSlot,
            material: material
        );

        return EditorSculptCommandModule.Echo(
            slot: slot,
            verb: "editor.sculpt.palette",
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"slot {Math.Clamp(
                    max: (CreationDocument.PaletteSize - 1),
                    min: 0,
                    value: paletteSlot
                )} rgb=({r:0.00}, {g:0.00}, {b:0.00})"
            )
        );
    }
    // The smooth fold: a leading up|down literal, or (with no token) a bound constant Axis1D value from the style
    // page's D-pad Up/Down step chord — see EditorCommandModule.TryDirection. Anything else falls through to the
    // original <v> form.
    private CommandResult SmoothHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "down",
            positive: "up"
        )) {
            var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: "editor.sculpt.smooth",
                session: m_session,
                workbench: m_workbench
            );

            if (error is { } benchError) {
                return benchError;
            }

            var stepped = model!.SetSmooth(value: (model.TargetSmooth + ((direction > 0)
                ? SmoothStep
                : -SmoothStep)));

            return EditorSculptCommandModule.Echo(
                slot: slot,
                verb: "editor.sculpt.smooth",
                detail: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"smooth={stepped:0.00}"
                )
            );
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.smooth: expected <v>, up, or down, plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(
            args: in args,
            at: 0,
            value: out var value
        )) {
            return CommandResult.Error(output: "[editor.sculpt.smooth: could not parse <v> as a finite number]");
        }

        var (valueSlot, valueModel, valueError) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 1,
            context: context,
            session: m_session,
            verb: "editor.sculpt.smooth",
            workbench: m_workbench
        );

        if (valueError is { } resolveError) {
            return resolveError;
        }

        var applied = valueModel!.SetSmooth(value: value);

        return EditorSculptCommandModule.Echo(
            slot: valueSlot,
            verb: "editor.sculpt.smooth",
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{applied:0.00}"
            )
        );
    }
    private static bool TryParseBlend(ReadOnlySpan<char> token, out SdfBlendOp blend) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "union"
        )) {
            blend = SdfBlendOp.Union;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "smoothunion"
        )) {
            blend = SdfBlendOp.SmoothUnion;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "subtract"
        )) {
            blend = SdfBlendOp.Subtraction;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "smoothsubtract"
        )) {
            blend = SdfBlendOp.SmoothSubtraction;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "intersect"
        )) {
            blend = SdfBlendOp.Intersection;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "smoothintersect"
        )) {
            blend = SdfBlendOp.SmoothIntersection;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "xor"
        )) {
            blend = SdfBlendOp.Xor;

            return true;
        }

        blend = SdfBlendOp.Union;

        return false;
    }
    private CommandResult UngroupHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: "editor.sculpt.ungroup",
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var released = model!.UngroupTarget();

        if (released == 0) {
            return CommandResult.Error(output: "[editor.sculpt.ungroup: the selected shape is not grouped]");
        }

        return EditorSculptCommandModule.Echo(
            detail: $"{released} shapes released to plain Union",
            slot: slot,
            verb: "editor.sculpt.ungroup"
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: BlendCommand,
            description: "Sets or cycles the TARGET's blend op (a non-Union blend coerces an ungrouped shape into its own group-of-one — blends only act within a group): editor.sculpt.blend [union|smoothunion|subtract|smoothsubtract|intersect|smoothintersect|xor|next|prev] [seat] (default next — the chord twin is South on the RT style page).",
            handler: BlendHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SmoothCommand,
            description: "Sets or steps the TARGET's smooth-blend radius (clamped 0..0.5): editor.sculpt.smooth <v> [seat], or editor.sculpt.smooth <up|down> [seat] (±0.05 — the typed twins of the style page's Smooth+/- D-pad chord, which binds this same verb with no argument, direction from the binding's constant value).",
            handler: SmoothHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MirrorCommand,
            description: "Toggles (or sets) the TARGET's local X=0 mirror fold: editor.sculpt.mirror [on|off] [seat]. The chord twin is North on the RT style page.",
            handler: MirrorHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MaterialCommand,
            description: "Assigns or cycles the TARGET's palette slot (0..15): editor.sculpt.material <slot> [seat], or editor.sculpt.material <next|prev> [seat] (the typed twins of the style page's Color+/- D-pad chord, which binds this same verb with no argument, direction from the binding's constant value). editor.sculpt.primitive is the same shape for the primitive type.",
            handler: MaterialHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.palette",
            description: "Edits a palette entry (every shape referencing the slot re-colors): editor.sculpt.palette <slot> <r> <g> <b> [emissive [specular [shininess]]] — channels 0..1; acts on the invoking seat's bench (no trailing seat token — the variable float run leaves it ambiguous).",
            handler: PaletteHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.link",
            description: "Links the SELECTED shape with the PREVIOUSLY selected one into a composition group (select A, select B, link — blends act within a group in document order): editor.sculpt.link [seat].",
            handler: LinkHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.ungroup",
            description: "Dissolves the SELECTED shape's group (every member returns to ungrouped plain Union): editor.sculpt.ungroup [seat].",
            handler: UngroupHandler
        );
    }

}
