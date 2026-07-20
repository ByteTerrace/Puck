using System.Globalization;
using System.Numerics;
using Puck.Authoring;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The sculpt STYLE console surface — the assist-layer twins of the RT style page's chords plus the numeric
/// setters: blend ops (with the group-of-one coercion), smooth radius, palette-slot assignment and palette-entry
/// editing, mirror, the twist/bend/dilate/onion field knobs, and group link/ungroup. All client-local model state.
/// A SEPARATE module to keep every class under its analyzer ceilings.
/// </summary>
internal sealed class EditorSculptStyleCommandModule(WorldEditorSession session, WorldWorkbench workbench) : ICommandModule {
    /// <summary>The blend-cycle act (South on the RT style page).</summary>
    public const string BlendCommand = "editor.sculpt.blend";
    /// <summary>The mirror-toggle act (North on the RT style page).</summary>
    public const string MirrorCommand = "editor.sculpt.mirror";
    /// <summary>The material-cycle-next act (East on the RT style page).</summary>
    public const string MaterialNextCommand = "editor.sculpt.material.next";
    /// <summary>The material-cycle-previous act (West on the RT style page).</summary>
    public const string MaterialPrevCommand = "editor.sculpt.material.prev";
    /// <summary>The smooth-step-up act (D-pad Up on the RT style page).</summary>
    public const string SmoothUpCommand = "editor.sculpt.smooth.up";
    /// <summary>The smooth-step-down act (D-pad Down on the RT style page).</summary>
    public const string SmoothDownCommand = "editor.sculpt.smooth.down";

    // The chord smooth step: one press moves the radius by a tenth of its envelope — act-scale, precision typed.
    private const float SmoothStep = 0.05f;

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: BlendCommand,
            description: "Sets or cycles the TARGET's blend op (a non-Union blend coerces an ungrouped shape into its own group-of-one — blends only act within a group): editor.sculpt.blend [union|smoothunion|subtract|smoothsubtract|intersect|smoothintersect|xor|next|prev] [seat] (default next — the chord twin is South on the RT style page).",
            handler: BlendHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.smooth",
            description: "Sets the TARGET's smooth-blend radius (clamped 0..0.5): editor.sculpt.smooth <v> [seat]. The chord twins are the style page's Smooth+/- steps.",
            handler: (context, args) => KnobHandler(context: context, args: in args, verb: "editor.sculpt.smooth", apply: static (model, value) => model.SetSmooth(value: value))
        );
        yield return CommandDefinition.WithWireArgs(
            name: SmoothUpCommand,
            description: "Steps the TARGET's smooth radius up 0.05: editor.sculpt.smooth.up [seat]. The chord twin is D-pad Up on the RT style page.",
            handler: (context, args) => SmoothStepHandler(context: context, args: in args, up: true, verb: SmoothUpCommand)
        );
        yield return CommandDefinition.WithWireArgs(
            name: SmoothDownCommand,
            description: "Steps the TARGET's smooth radius down 0.05: editor.sculpt.smooth.down [seat]. The chord twin is D-pad Down on the RT style page.",
            handler: (context, args) => SmoothStepHandler(context: context, args: in args, up: false, verb: SmoothDownCommand)
        );
        yield return CommandDefinition.WithWireArgs(
            name: MirrorCommand,
            description: "Toggles (or sets) the TARGET's local X=0 mirror fold: editor.sculpt.mirror [on|off] [seat]. The chord twin is North on the RT style page.",
            handler: MirrorHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.material",
            description: "Assigns the TARGET's palette slot (0..15): editor.sculpt.material <slot> [seat]. The chord twins are the style page's Color+/- cycles.",
            handler: MaterialHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: MaterialNextCommand,
            description: "Cycles the TARGET's palette slot forward: editor.sculpt.material.next [seat]. The chord twin is East on the RT style page.",
            handler: (context, args) => MaterialCycleHandler(context: context, args: in args, direction: 1, verb: MaterialNextCommand)
        );
        yield return CommandDefinition.WithWireArgs(
            name: MaterialPrevCommand,
            description: "Cycles the TARGET's palette slot backward: editor.sculpt.material.prev [seat]. The chord twin is West on the RT style page.",
            handler: (context, args) => MaterialCycleHandler(context: context, args: in args, direction: -1, verb: MaterialPrevCommand)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.palette",
            description: "Edits a palette entry (every shape referencing the slot re-colors): editor.sculpt.palette <slot> <r> <g> <b> [emissive [specular [shininess]]] — channels 0..1; acts on the invoking seat's bench (no trailing seat token — the variable float run leaves it ambiguous).",
            handler: PaletteHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.twist",
            description: "Sets the TARGET's twist rate about local Y (clamped ±3): editor.sculpt.twist <v> [seat].",
            handler: (context, args) => KnobHandler(context: context, args: in args, verb: "editor.sculpt.twist", apply: static (model, value) => model.SetTwist(value: value))
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.bend",
            description: "Sets the TARGET's bend rate about local Y (clamped ±1.5): editor.sculpt.bend <v> [seat].",
            handler: (context, args) => KnobHandler(context: context, args: in args, verb: "editor.sculpt.bend", apply: static (model, value) => model.SetBend(value: value))
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.dilate",
            description: "Sets the TARGET's dilate (inflation) radius (clamped 0..0.2): editor.sculpt.dilate <v> [seat].",
            handler: (context, args) => KnobHandler(context: context, args: in args, verb: "editor.sculpt.dilate", apply: static (model, value) => model.SetDilate(value: value))
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.onion",
            description: "Sets the TARGET's onion shell thickness (0 = solid, clamped 0..0.2): editor.sculpt.onion <v> [seat].",
            handler: (context, args) => KnobHandler(context: context, args: in args, verb: "editor.sculpt.onion", apply: static (model, value) => model.SetOnion(value: value))
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.link",
            description: "Links the SELECTED shape with the PREVIOUSLY selected one into a composition group (select A, select B, link — blends act within a group in document order): editor.sculpt.link [seat].",
            handler: LinkHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "editor.sculpt.ungroup",
            description: "Dissolves the SELECTED shape's group (every member returns to ungrouped plain Union): editor.sculpt.ungroup [seat].",
            handler: UngroupHandler
        );
    }

    private CommandResult BlendHandler(CommandContext context, WireArgs args) {
        var hasToken = ((args.Count >= 1) && !int.TryParse(s: args[0], result: out _));
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: (hasToken ? 1 : 0), verb: BlendCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        SdfBlendOp applied;

        if (!hasToken || args.Is(index: 0, value: "next")) {
            applied = model!.CycleBlend(direction: 1);
        } else if (args.Is(index: 0, value: "prev")) {
            applied = model!.CycleBlend(direction: -1);
        } else if (TryParseBlend(token: args[0], blend: out var parsed)) {
            model!.SetBlend(blend: parsed);
            applied = parsed;
        } else {
            return Error(text: $"[{BlendCommand}: unknown op '{args[0].ToString()}' — union|smoothunion|subtract|smoothsubtract|intersect|smoothintersect|xor|next|prev]");
        }

        return Echo(slot: slot, verb: BlendCommand, detail: $"{applied}");
    }

    private CommandResult SmoothStepHandler(CommandContext context, in WireArgs args, bool up, string verb) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var applied = model!.SetSmooth(value: (model.TargetSmooth + (up ? SmoothStep : -SmoothStep)));

        return Echo(slot: slot, verb: verb, detail: string.Create(provider: CultureInfo.InvariantCulture, handler: $"smooth={applied:0.00}"));
    }

    private CommandResult MirrorHandler(CommandContext context, WireArgs args) {
        var hasToken = ((args.Count >= 1) && !int.TryParse(s: args[0], result: out _));
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: (hasToken ? 1 : 0), verb: MirrorCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        bool applied;

        if (hasToken && args.Is(index: 0, value: "on")) {
            applied = (model!.TargetMirror || model.ToggleMirror());
        } else if (hasToken && args.Is(index: 0, value: "off")) {
            applied = (model!.TargetMirror && model.ToggleMirror());
        } else if (hasToken) {
            return Error(text: $"[{MirrorCommand}: expected on, off, or nothing (toggle)]");
        } else {
            applied = model!.ToggleMirror();
        }

        return Echo(slot: slot, verb: MirrorCommand, detail: $"mirror {(applied ? "on" : "off")}");
    }

    private CommandResult MaterialHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.material: expected <slot 0..15> plus an optional seat 1..4]");
        }

        if (!args.TryInt(index: 0, value: out var requested)) {
            return Error(text: "[editor.sculpt.material: could not parse <slot> as an integer]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 1, verb: "editor.sculpt.material", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        return Echo(slot: slot, verb: "editor.sculpt.material", detail: $"slot {model!.SetMaterialIndex(index: requested)}");
    }

    private CommandResult MaterialCycleHandler(CommandContext context, in WireArgs args, int direction, string verb) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        return Echo(slot: slot, verb: verb, detail: $"slot {model!.CycleMaterial(direction: direction)}");
    }

    private CommandResult PaletteHandler(CommandContext context, WireArgs args) {
        // Shapes: <slot> <r> <g> <b> [emissive [specular [shininess]]] — acts on the invoking seat's bench (the
        // variable float run makes a trailing seat token ambiguous, so this verb deliberately takes none).
        if (args.Count is (< 4 or > 7)) {
            return Error(text: "[editor.sculpt.palette: expected <slot> <r> <g> <b> [emissive [specular [shininess]]]]");
        }

        if (!args.TryInt(index: 0, value: out var paletteSlot)) {
            return Error(text: "[editor.sculpt.palette: could not parse <slot> as an integer]");
        }

        if (!EditorCommandModule.TryFloat(args: in args, at: 1, value: out var r) ||
            !EditorCommandModule.TryFloat(args: in args, at: 2, value: out var g) ||
            !EditorCommandModule.TryFloat(args: in args, at: 3, value: out var b)) {
            return Error(text: "[editor.sculpt.palette: could not parse <r> <g> <b> as finite numbers]");
        }

        var extras = new float[3];
        var extraCount = 0;

        for (var at = 4; (at < args.Count); at++) {
            if (!EditorCommandModule.TryFloat(args: in args, at: at, value: out extras[extraCount])) {
                return Error(text: $"[editor.sculpt.palette: could not parse '{args[at].ToString()}' as a finite number]");
            }

            extraCount++;
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: WireArgs.Empty, at: 0, verb: "editor.sculpt.palette", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var material = new SdfMaterial(Albedo: new Vector3(x: Math.Clamp(value: r, min: 0f, max: 1f), y: Math.Clamp(value: g, min: 0f, max: 1f), z: Math.Clamp(value: b, min: 0f, max: 1f)));

        if (extraCount >= 1) {
            material = (material with { Emissive = MathF.Max(x: extras[0], y: 0f) });
        }

        if (extraCount >= 2) {
            material = (material with { Specular = MathF.Max(x: extras[1], y: 0f) });
        }

        if (extraCount >= 3) {
            material = (material with { Shininess = MathF.Max(x: extras[2], y: 1f) });
        }

        model!.SetPaletteEntry(index: paletteSlot, material: material);

        return Echo(slot: slot, verb: "editor.sculpt.palette", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"slot {Math.Clamp(value: paletteSlot, min: 0, max: (CreationDocument.PaletteSize - 1))} rgb=({r:0.00}, {g:0.00}, {b:0.00})"
        ));
    }

    private CommandResult LinkHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: "editor.sculpt.link", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        if (model!.LinkWithPrevious() is not { } groupId) {
            return Error(text: "[editor.sculpt.link: needs two distinct selections in a row (select A, select B, link)]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.link", detail: $"group {groupId}");
    }

    private CommandResult UngroupHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 0, verb: "editor.sculpt.ungroup", session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var released = model!.UngroupTarget();

        if (released == 0) {
            return Error(text: "[editor.sculpt.ungroup: the selected shape is not grouped]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.ungroup", detail: $"{released} shapes released to plain Union");
    }

    // The shared clamped-knob handler (smooth/twist/bend/dilate/onion share the <v> [seat] shape).
    private CommandResult KnobHandler(CommandContext context, in WireArgs args, string verb, Func<SculptModel, float, float> apply) {
        if (args.Count is (< 1 or > 2)) {
            return Error(text: $"[{verb}: expected <v> plus an optional seat 1..4]");
        }

        if (!EditorCommandModule.TryFloat(args: in args, at: 0, value: out var value)) {
            return Error(text: $"[{verb}: could not parse <v> as a finite number]");
        }

        var (slot, model, error) = EditorSculptCommandModule.ResolveBench(context: context, args: in args, at: 1, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var applied = apply(model!, value);

        return Echo(slot: slot, verb: verb, detail: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{applied:0.00}"));
    }

    private static bool TryParseBlend(ReadOnlySpan<char> token, out SdfBlendOp blend) {
        if (token.Equals(other: "union", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.Union;

            return true;
        }

        if (token.Equals(other: "smoothunion", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.SmoothUnion;

            return true;
        }

        if (token.Equals(other: "subtract", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.Subtraction;

            return true;
        }

        if (token.Equals(other: "smoothsubtract", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.SmoothSubtraction;

            return true;
        }

        if (token.Equals(other: "intersect", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.Intersection;

            return true;
        }

        if (token.Equals(other: "smoothintersect", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.SmoothIntersection;

            return true;
        }

        if (token.Equals(other: "xor", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            blend = SdfBlendOp.Xor;

            return true;
        }

        blend = SdfBlendOp.Union;

        return false;
    }

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
