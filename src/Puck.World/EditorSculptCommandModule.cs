using System.Globalization;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.Forge.Authoring;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The sculpt console surface: lifecycle (<c>new</c>/<c>edit</c>/<c>exit</c>/<c>commit</c>/<c>easel</c>/<c>status</c>/
/// <c>undo</c>/<c>redo</c>/<c>zoom</c>), the shape/style gestures (<c>add</c>/<c>remove</c>/<c>duplicate</c>/
/// <c>select</c>/<c>deselect</c>/<c>primitive</c>/<c>material</c>/<c>blend</c>/<c>scale</c>/<c>link</c>/
/// <c>ungroup</c>), and the ONE generic field door — <c>editor.sculpt.set &lt;path&gt; &lt;json&gt;</c> /
/// <c>editor.sculpt.remove &lt;path&gt;</c> — the creation-scoped twin of <c>world.row.set</c>/<c>world.row.remove</c>
/// (<see cref="WorldRowCommandModule"/>). <c>editor.sculpt.new</c>/<c>edit</c> open a seat's bench (a blank model, or
/// an existing creation row loaded through the canonical pipeline) and flip its active binding group onto the sculpt
/// pages; <c>editor.sculpt.commit</c> canonicalizes the model and submits ONE <c>UpsertCreation</c> (doc + hash
/// always come from the same canonicalize call); <c>editor.sculpt.easel</c> authors the diegetic preview easel;
/// <c>editor.sculpt.undo</c>/<c>redo</c> walk the LOCAL ring (the world journal is untouched). Every remaining
/// per-field verb (bend/dilate/onion/twist/rotate/move/nudge/rename/mirror/palette/smooth/the typed scale and
/// material forms) is gone — <c>editor.sculpt.set .&lt;field&gt; &lt;json&gt;</c> (the selected shape, a targeted
/// chain goal, or a flat brush field when nothing is selected) or a bare document path (<c>shapes[3].scale</c>,
/// <c>palette[0].color</c>) covers all of it, generically, through <see cref="SculptModel.TrySet"/>. Primitive/
/// material/blend/scale keep their CYCLE/STEP grammar (genuinely gestural — a pad chord still drives them); their
/// typed literal-value forms are gone the same way.
/// </summary>
/// <remarks><c>new</c>/<c>edit</c>/<c>exit</c>/<c>commit</c>/<c>easel</c> route Simulation (they follow a sim-routed
/// <c>editor.enter</c> in a scripted burst, and commit/easel submit mutations the stdin barrier then serializes
/// reads behind); <c>set</c> is ALSO Simulation-routed — not because it submits a mutation (it never does), but
/// because a line carrying a quoted JSON tail only keeps its quotes through the reconstruction an Immediate
/// dispatch's System.CommandLine fallback loses them on; Simulation re-plays the ORIGINAL line text instead. Every
/// other verb here is pure client state, carries no quoted payload, and stays Immediate.</remarks>
internal sealed class EditorSculptCommandModule(WorldEditorSession session, WorldWorkbench workbench, WorldSeatBindings seatBindings, WorldClient client, IServerLink link) : ICommandModule {
    private const float EaselFieldOfViewRadians = 0.9f;
    private const uint EaselRenderHeight = 240;
    private const uint EaselRenderWidth = 320;

    /// <summary>The add act (South on the sculpt resting page): the brush's primitive at the spawn point.</summary>
    public const string AddCommand = EditorSculptShapeCommandNames.AddCommand;
    /// <summary>The blend-cycle act (South on the RT style page).</summary>
    public const string BlendCommand = EditorSculptStyleCommandNames.BlendCommand;
    /// <summary>The commit act (North on the LT bench page): one canonicalized UpsertCreation.</summary>
    public const string CommitCommand = EditorSculptCommandNames.CommitCommand;
    /// <summary>The deselect act (West on the LT bench page): the target reverts to the brush.</summary>
    public const string DeselectCommand = EditorSculptShapeCommandNames.DeselectCommand;
    /// <summary>The duplicate act (D-pad Up on the sculpt resting page).</summary>
    public const string DuplicateCommand = EditorSculptShapeCommandNames.DuplicateCommand;
    /// <summary>The easel act (South on the LT bench page): the diegetic preview screen + camera pair.</summary>
    public const string EaselCommand = EditorSculptCommandNames.EaselCommand;
    /// <summary>The bench-exit act (Back on the sculpt resting page; the sculpt wheel's Done sector).</summary>
    public const string ExitCommand = EditorSculptCommandNames.ExitCommand;
    /// <summary>The material verb: <c>editor.sculpt.material &lt;next|prev&gt;</c>. East/West on the RT style page
    /// bind it with a constant Axis1D value (+1 next, -1 prev) in place of an argument.</summary>
    public const string MaterialCommand = EditorSculptStyleCommandNames.MaterialCommand;
    /// <summary>The primitive-cycle act (North on the sculpt resting page).</summary>
    public const string PrimitiveCommand = EditorSculptShapeCommandNames.PrimitiveCommand;
    /// <summary>The local-ring redo act (East on the sculpt resting page).</summary>
    public const string RedoCommand = EditorSculptCommandNames.RedoCommand;
    /// <summary>The delete act — bare (D-pad Down on the sculpt resting page) deletes the SELECTED shape; with a
    /// path argument (<c>editor.sculpt.remove shapes[3]</c>) removes any document row by index.</summary>
    public const string RemoveCommand = EditorSculptShapeCommandNames.RemoveCommand;
    /// <summary>The scale step verb: <c>editor.sculpt.scale &lt;grow|shrink&gt;</c>. D-pad Right/Left on the RT
    /// style page bind it with a constant Axis1D value (+1 grow, -1 shrink) in place of an argument.</summary>
    public const string ScaleCommand = EditorSculptShapeCommandNames.ScaleCommand;
    /// <summary>The select verb, widened to fold the target cycle onto itself: <c>editor.sculpt.select
    /// &lt;id|name|next|prev&gt;</c>. The cycle extends past shapes into chain goals.</summary>
    public const string SelectCommand = EditorSculptShapeCommandNames.SelectCommand;
    /// <summary>The local-ring undo act (West on the sculpt resting page).</summary>
    public const string UndoCommand = EditorSculptCommandNames.UndoCommand;
    /// <summary>The zoom verb: <c>editor.sculpt.zoom &lt;in|out|distance&gt;</c>.</summary>
    public const string ZoomCommand = EditorSculptCommandNames.ZoomCommand;

    // The easel's fixed vantage/screen offsets from the workbench origin — one deliberate diagonal that frames the
    // bench envelope (pivot lift 1, model bound ±6) inside the offscreen view, and a slab spot beside the bench
    // that never occludes that vantage.
    private static readonly Vector3 EaselEyeOffset = new(
        x: 2.6f,
        y: 2.2f,
        z: 3.6f
    );
    private static readonly Vector3 EaselLookOffset = new(
        x: 0f,
        y: 1f,
        z: 0f
    );
    private static readonly Vector3 EaselScreenOffset = new(
        x: -2.6f,
        y: 1.5f,
        z: 1.4f
    );
    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;

    /// <summary>Formats a seat-scoped editor command result for the transcript.</summary>
    /// <param name="slot">The zero-based player slot.</param>
    /// <param name="verb">The command name.</param>
    /// <param name="detail">The command result detail.</param>
    /// <returns>The formatted command result.</returns>
    internal static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");
    /// <summary>Resolves the acting seat and its OPEN bench model for a sculpt verb, sharing the editor slot
    /// convention (trailing [seat] token authoritative).</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <param name="session">The editor session (the mode guard).</param>
    /// <param name="workbench">The workbench (the bench guard).</param>
    /// <returns>The slot and model, or an error result.</returns>
    internal static (int Slot, SculptModel? Model, CommandResult? Error) ResolveBench(CommandContext context, in WireArgs args, int at, string verb, WorldEditorSession session, WorldWorkbench workbench) {
        var (slot, error) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: at,
            context: context,
            verb: verb
        );

        if (error is { } resolveError) {
            return (Slot: slot, Model: null, Error: resolveError);
        }

        if (!session.IsEditing(slot: slot)) {
            return (Slot: slot, Model: null, Error: CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]"));
        }

        if (workbench.Model(slot: slot) is not { } model) {
            return (Slot: slot, Model: null, Error: CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt — editor.sculpt.new <rowId> or editor.sculpt.edit <rowId> first]"));
        }

        return (Slot: slot, Model: model, Error: null);
    }
    // Whether a lone token reads as a seat number (1..4) — the multi-form verbs' path-vs-seat discriminator.
    internal static bool SeatToken(ReadOnlySpan<char> token) =>
        (int.TryParse(
            s: token,
            provider: CultureInfo.InvariantCulture,
            result: out var value
        ) && (value is >= 1 and <= PlayerRoster.MaxSlots));

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
        var hasPosition = ((args.Count >= (positionAt + 3)) && EditorCommandModule.TryFloat(
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

        var (slot, model, error) = ResolveBench(
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

        return Echo(
            slot: slot,
            verb: AddCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00}) selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
            )
        );
    }
    // The open-bench guard for verbs that resolved their seat separately (an explicit trailing token stays honored).
    private CommandResult? BenchGuard(int slot, string verb) {
        if (!m_session.IsEditing(slot: slot)) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
        }

        if (!m_workbench.IsActive(slot: slot)) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt — editor.sculpt.new <rowId> first]");
        }

        return null;
    }
    private CommandResult BlendHandler(CommandContext context, WireArgs args) {
        var hasDirectionToken = ((args.Count >= 1) && (args.Is(
            index: 0,
            value: "next"
        ) || args.Is(
            index: 0,
            value: "prev"
        )));

        if (
            (args.Count >= 1) &&
            !hasDirectionToken &&
            !SeatToken(token: args[0])
        ) {
            return CommandResult.Error(output: $"[{BlendCommand}: expected next, prev, or nothing — editor.sculpt.set .blend \"<op>\" sets it directly]");
        }

        var (slot, model, error) = ResolveBench(
            args: in args,
            at: (hasDirectionToken
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

        var direction = ((hasDirectionToken && args.Is(
            index: 0,
            value: "prev"
        ))
            ? -1
            : 1
        );
        var applied = model!.CycleBlend(direction: direction);

        return Echo(
            detail: $"{applied}",
            slot: slot,
            verb: BlendCommand
        );
    }
    private CommandResult CommitHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: CommitCommand,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var rowId = m_workbench.RowId(slot: slot);
        CanonicalDocument<CreationDocument> canonical;

        try {
            canonical = CreationCanonicalizer.Canonicalize(
                document: model!.Document,
                source: rowId
            );
        } catch (DocumentValidationException exception) {
            return CommandResult.Error(output: $"[{CommitCommand}: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }

        // Doc + hash from the SAME canonical result — the hash-provenance contract, satisfied structurally.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: context.ActingPrincipal(),
            Creation: new WorldCreation(
                Id: rowId,
                Document: canonical.Document,
                HashRaw: canonical.Hash
            )
        ));
        m_workbench.NoteCommitSubmitted(
            slot: slot,
            hash: canonical.Hash
        );

        return Echo(
            slot: slot,
            verb: CommitCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"'{rowId}' sha256 {canonical.Hash[..12]}… ({canonical.Document.StampShapeCount()} stamp shapes, {(canonical.Document.Frames?.Count ?? 0)} frames) — one UpsertCreation submitted (clean on server accept); world.undo reverts it, editor.sculpt.undo stays local"
            )
        );
    }
    // The target readout the cycle/add echoes share.
    private static string DescribeTarget(SculptModel model) {
        if (model.TargetGoalChain is { } chain) {
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
        var (slot, model, error) = ResolveBench(
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

        return Echo(
            detail: "target=brush",
            slot: slot,
            verb: DeselectCommand
        );
    }
    private CommandResult DuplicateHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = ResolveBench(
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

        var twin = model.SelectedShape!;

        return Echo(
            slot: slot,
            verb: DuplicateCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"twin shape {twin.Id} selected — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes"
            )
        );
    }
    private CommandResult EaselHandler(CommandContext context, WireArgs args) {
        // Shapes: none = default screen + acting seat; [screenIndex] and/or trailing [seat].
        var hasIndex = ((args.Count >= 2) || ((args.Count == 1) && !SeatToken(token: args[0])));

        var (slot, error) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: (hasIndex
            ? 1
            : 0),
            context: context,
            verb: EaselCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (BenchGuard(
            slot: slot,
            verb: EaselCommand
        ) is { } guard) {
            return guard;
        }

        var screens = m_client.Definition.Screens;

        if (screens.Count == 0) {
            return CommandResult.Error(output: $"[{EaselCommand}: the world declares no screen rows — author one with world.row.set screens <json> first (runtime screens need a declared index)]");
        }

        WorldScreen? target = null;

        if (hasIndex) {
            if (!args.TryInt(
                index: 0,
                value: out var index
            )) {
                return CommandResult.Error(output: $"[{EaselCommand}: could not parse screen index '{args[0].ToString()}']");
            }

            foreach (var screen in screens) {
                if (screen.Index == index) {
                    target = screen;

                    break;
                }
            }

            if (target is null) {
                return CommandResult.Error(output: $"[{EaselCommand}: no screen row with index {index} — see world.screens]");
            }
        } else {
            target = screens[0];
        }

        var origin = m_workbench.Origin(slot: slot);
        var cameraName = $"easel-{PlayerRoster.DisplayNumber(slot: slot)}";
        var principal = context.ActingPrincipal();

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCamera(
            Principal: principal,
            Camera: new WorldCamera(
                Name: cameraName,
                Anchor: null,
                Rig: new WorldCameraRig(
                    Motion: new WorldCameraMotion.Static(
                        Position: (origin + EaselEyeOffset),
                        WorldAxes: true
                    ),
                    Aim: new WorldCameraAim.WorldPoint(Target: (origin + EaselLookOffset)),
                    Lens: new WorldCameraLens(FieldOfViewRadians: EaselFieldOfViewRadians)
                ),
                RenderWidth: EaselRenderWidth,
                RenderHeight: EaselRenderHeight
            )
        ));
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(
            Principal: principal,
            Screen: (target with {
                Origin = (origin + EaselScreenOffset),
                Source = new WorldScreenSource.View(CameraName: cameraName),
            })
        ));

        return Echo(
            detail: $"camera '{cameraName}' + screen {target.Index} re-pointed at its view beside the bench — two mutations submitted (world.undo twice retires the easel)",
            slot: slot,
            verb: EaselCommand
        );
    }
    private CommandResult EditHandler(CommandContext context, WireArgs args) =>
        OpenHandler(
            args: in args,
            context: context,
            loadExisting: true,
            verb: "editor.sculpt.edit"
        );
    private CommandResult ExitHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: ExitCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!m_workbench.IsActive(slot: slot)) {
            return CommandResult.Error(output: $"[{ExitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt]");
        }

        var rowId = m_workbench.RowId(slot: slot);
        var discarded = m_workbench.UncommittedEdits(slot: slot);

        _ = m_workbench.Drop(slot: slot);

        m_seatBindings.SetContextState(
            family: WorldContextFamilies.Editor,
            slot: slot,
            state: WorldContextFamilies.EditorEditing
        );

        var regrouped = m_seatBindings.ContextGroup(
            family: WorldContextFamilies.Editor,
            slot: slot,
            state: WorldContextFamilies.EditorEditing
        );
        var groupNote = ((regrouped is not null)
            ? $" — group {regrouped}"
            : $" — WARNING: this seat's document maps no group to the {WorldContextFamilies.Editor}={WorldContextFamilies.EditorEditing} context, so the seat keeps its current page"
        );

        return Echo(
            detail: $"closed '{rowId}'{((discarded > 0)
            ? $" ({discarded} uncommitted edits discarded)"
            : string.Empty)}{groupNote}",
            slot: slot,
            verb: ExitCommand
        );
    }
    private CommandResult LinkHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = ResolveBench(
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

        return Echo(
            detail: $"group {groupId}",
            slot: slot,
            verb: "editor.sculpt.link"
        );
    }
    private CommandResult MaterialHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "prev",
            positive: "next"
        )) {
            var (slot, model, error) = ResolveBench(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: MaterialCommand,
                session: m_session,
                workbench: m_workbench
            );

            if (error is { } benchError) {
                return benchError;
            }

            return Echo(
                slot: slot,
                verb: MaterialCommand,
                detail: $"slot {model!.CycleMaterial(direction: direction)}"
            );
        }

        return CommandResult.Error(output: $"[{MaterialCommand}: expected next, prev, or nothing (a bound chord) — editor.sculpt.set .material <slot> sets it directly]");
    }
    private CommandResult NewHandler(CommandContext context, WireArgs args) =>
        OpenHandler(
            args: in args,
            context: context,
            loadExisting: false,
            verb: "editor.sculpt.new"
        );
    // The shared bench-open flow: resolve the row id + optional explicit origin + seat, guard the mode, load or
    // refuse against the existing rows, envelope-check the composed preview, then flip the binding group and seed
    // the orbit.
    private CommandResult OpenHandler(CommandContext context, in WireArgs args, string verb, bool loadExisting) {
        if (args.Count is (< 1 or > 5)) {
            return CommandResult.Error(output: $"[{verb}: expected <rowId> [<x> <y> <z>] [seat]]");
        }

        var rowId = args[0].ToString();
        var hasPosition = (args.Count >= 4);

        var x = 0f;
        var y = 0f;
        var z = 0f;

        if (
            hasPosition &&
            (!EditorCommandModule.TryFloat(
            args: in args,
            at: 1,
            value: out x
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 2,
            value: out y
        ) ||
            !EditorCommandModule.TryFloat(
            args: in args,
            at: 3,
            value: out z
        ))
        ) {
            return CommandResult.Error(output: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: (hasPosition
            ? 4
            : 1),
            context: context,
            verb: verb
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!m_session.IsEditing(slot: slot)) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
        }

        var existing = WorldDefinitionRows.FindCreation(
            creations: m_client.Definition.Creations,
            id: rowId
        );
        CreationDocument? document = null;

        if (loadExisting) {
            if (existing is not { } row) {
                return CommandResult.Error(output: $"[{verb}: no creation row '{rowId}' — see editor.creations, or editor.sculpt.new {rowId} starts blank]");
            }

            document = row.Document;
        } else if (existing is not null) {
            return CommandResult.Error(output: $"[{verb}: creation row '{rowId}' already exists — editor.sculpt.edit {rowId} loads it, or pick a new id]");
        }

        var focus = m_session.Focus(slot: slot);
        var origin = (hasPosition
            ? new Vector3(
                x: x,
                y: y,
                z: z
            )
            : focus
        );

        if (!m_workbench.TryEnter(
            document: document,
            error: out var enterError,
            origin: origin,
            rowId: rowId,
            slot: slot
        )) {
            return CommandResult.Error(output: $"[{verb}: {enterError}]");
        }

        if (m_seatBindings.ContextGroup(
            family: WorldContextFamilies.Editor,
            slot: slot,
            state: WorldContextFamilies.EditorSculpting
        ) is null) {
            _ = m_workbench.Drop(slot: slot);

            return CommandResult.Error(output: $"[{verb}: this seat's document maps no group to the {WorldContextFamilies.Editor}={WorldContextFamilies.EditorSculpting} context, so the bench has no page to resolve its verbs against — the bench was not opened]");
        }

        m_seatBindings.SetContextState(
            family: WorldContextFamilies.Editor,
            slot: slot,
            state: WorldContextFamilies.EditorSculpting
        );

        m_session.SeedWorkbenchOrbit(slot: slot);

        var model = m_workbench.Model(slot: slot)!;

        return Echo(
            slot: slot,
            verb: verb,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"sculpting '{rowId}' at ({origin.X:0.00}, {origin.Y:0.00}, {origin.Z:0.00}) — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes, group sculpt (LT bench, RT style, LT+RT frames, RT+LT rig)"
            )
        );
    }
    private CommandResult PrimitiveHandler(CommandContext context, WireArgs args) {
        var hasDirectionToken = ((args.Count >= 1) && (args.Is(
            index: 0,
            value: "next"
        ) || args.Is(
            index: 0,
            value: "prev"
        )));

        if (
            (args.Count >= 1) &&
            !hasDirectionToken &&
            !SeatToken(token: args[0])
        ) {
            return CommandResult.Error(output: $"[{PrimitiveCommand}: expected next, prev, or nothing — editor.sculpt.set .type \"<name>\" sets it directly]");
        }

        var (slot, model, error) = ResolveBench(
            args: in args,
            at: (hasDirectionToken
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

        var direction = ((hasDirectionToken && args.Is(
            index: 0,
            value: "prev"
        ))
            ? -1
            : 1
        );
        var applied = model!.CyclePrimitive(direction: direction);

        return Echo(
            detail: $"{applied} ({(model.TargetIsBrush
            ? "brush — the next add"
            : "selected shape")})",
            slot: slot,
            verb: PrimitiveCommand
        );
    }
    private CommandResult RemoveHandler(CommandContext context, WireArgs args) {
        var hasPath = ((args.Count >= 1) && !SeatToken(token: args[0]));

        if (!hasPath) {
            var (slot, model, error) = ResolveBench(
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
                return CommandResult.Error(output: $"[{RemoveCommand}: no shape selected — editor.sculpt.select first, or pass a path: {RemoveCommand} shapes[3]]");
            }

            return Echo(
                detail: $"shape removed — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes",
                slot: slot,
                verb: RemoveCommand
            );
        }

        // The path form acts on the invoking seat's own bench — no trailing seat token (same convention as every
        // other variable-tail sculpt verb: editor.sculpt.chain's named form, editor.sculpt.palette).
        var (pathSlot, pathModel, pathError) = ResolveBench(
            context: context,
            args: WireArgs.Empty,
            at: 0,
            verb: RemoveCommand,
            session: m_session,
            workbench: m_workbench
        );

        if (pathError is { } resolveError) {
            return resolveError;
        }

        var path = args[0].ToString();
        var outcome = pathModel!.TryRemove(path: path);

        return (outcome.Success
            ? Echo(
                detail: $"{path} removed",
                slot: pathSlot,
                verb: RemoveCommand
            )
            : CommandResult.Error(output: $"[{RemoveCommand}: {outcome.Message}]")
        );
    }
    private CommandResult RingHandler(CommandContext context, in WireArgs args, bool redo, string verb) {
        var (slot, model, error) = ResolveBench(
            args: in args,
            at: 0,
            context: context,
            session: m_session,
            verb: verb,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var applied = (redo
            ? model!.Redo()
            : model!.Undo()
        );

        if (!applied) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} nothing to {(redo
                ? "redo"
                : "undo")} on the local ring]");
        }

        return Echo(
            detail: $"local ring — restored ({(model.CanUndo
            ? "more undo available"
            : "at the baseline")}); world journal untouched",
            slot: slot,
            verb: verb
        );
    }
    private CommandResult ScaleHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "shrink",
            positive: "grow"
        )) {
            var (slot, model, error) = ResolveBench(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: ScaleCommand,
                session: m_session,
                workbench: m_workbench
            );

            if (error is { } benchError) {
                return benchError;
            }

            var applied = model!.StepScale(direction: direction);

            return Echo(
                slot: slot,
                verb: ScaleCommand,
                detail: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"scale=({applied.X:0.00}, {applied.Y:0.00}, {applied.Z:0.00})"
                )
            );
        }

        return CommandResult.Error(output: $"[{ScaleCommand}: expected grow, shrink, or nothing (a bound chord) — editor.sculpt.set .scale [x,y,z] sets it directly]");
    }
    // The select fold: no args -> bound dispatch (direction from a constant Axis1D value); a leading next|prev
    // literal -> the same cycle, typed; otherwise the explicit <id|name> [seat] form.
    private CommandResult SelectHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return SelectNoArgHandler(context: context);
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
            var (slot, model, error) = ResolveBench(
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

            model!.CycleSelection(direction: (args.Is(
                index: 0,
                value: "next"
            )
                ? 1
                : -1));

            return Echo(
                slot: slot,
                verb: SelectCommand,
                detail: DescribeTarget(model: model)
            );
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.select: expected <id|name>, next, prev, plus an optional seat 1..4]");
        }

        var (idSlot, idModel, idError) = ResolveBench(
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

        return Echo(
            slot: idSlot,
            verb: SelectCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00})"
            )
        );
    }
    // CommandContext.Origin is the discriminator, not Value.Kind — this verb declares Axis1D, so the text path's
    // own impulse value would read Axis1D too. See CommandDefinition.WithWireArgs's doctrine comment.
    private CommandResult SelectNoArgHandler(CommandContext context) {
        var (slot, model, error) = ResolveBench(
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

        model!.CycleSelection(direction: ((context.Value.AsAxis1D >= 0f)
            ? 1
            : -1));

        return Echo(
            slot: slot,
            verb: SelectCommand,
            detail: DescribeTarget(model: model)
        );
    }
    private CommandResult SetHandler(CommandContext context, WireArgs args) {
        if (args.Count < 1) {
            return Usage(
                form: "<path> <json>",
                verb: "editor.sculpt.set"
            );
        }

        // No trailing seat token — the JSON tail is free text and could itself end in a bare integer, so this verb
        // (like the chain/palette named forms) always acts on the invoking seat's own bench.
        var (slot, model, error) = ResolveBench(
            context: context,
            args: WireArgs.Empty,
            at: 0,
            verb: "editor.sculpt.set",
            session: m_session,
            workbench: m_workbench
        );

        if (error is { } benchError) {
            return benchError;
        }

        var path = args[0].ToString();
        var json = WorldCommandArguments.RawAfter(
            args: in args,
            context: context,
            tokens: 2
        );

        if (string.IsNullOrWhiteSpace(value: json)) {
            return Usage(
                form: $"{path} <json>",
                verb: "editor.sculpt.set"
            );
        }

        var outcome = model!.TrySet(
            json: json,
            path: path
        );

        return (outcome.Success
            ? Echo(
                slot: slot,
                verb: "editor.sculpt.set",
                detail: outcome.Message
            )
            : CommandResult.Error(output: $"[editor.sculpt.set: {outcome.Message}]")
        );
    }
    private CommandResult StatusHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: "editor.sculpt.status"
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        var seat = PlayerRoster.DisplayNumber(slot: slot);

        if (m_workbench.Model(slot: slot) is not { } model) {
            return new CommandResult(Output: $"[editor.sculpt.status: seat {seat} no open sculpt — editor.sculpt.new <rowId> starts one]");
        }

        var origin = m_workbench.Origin(slot: slot);
        var target = "target=brush";

        if (model.TargetGoalChain is { } chain) {
            target = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"target=goal chain {chain.Id} at ({chain.Goal.X:0.00}, {chain.Goal.Y:0.00}, {chain.Goal.Z:0.00})"
            );
        } else if (model.SelectedShape is { } shape) {
            target = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"target=shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00})"
            );
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.sculpt.status: seat {seat} sculpting '{m_workbench.RowId(slot: slot)}' shapes {model.StampShapeCount}/{model.ShapeCapacity} {target} frame {model.CurrentFrame}/{model.FrameCount}{(model.Playing
            ? " playing"
            : string.Empty)} chains {model.Chains.Count} ring {model.HistoryCount}/{SculptModel.HistoryCapacity} uncommitted {m_workbench.UncommittedEdits(slot: slot)}{(m_workbench.IsCommitPending(slot: slot)
            ? " commit=pending"
            : string.Empty)} origin=({origin.X:0.00}, {origin.Y:0.00}, {origin.Z:0.00})]"
        ));
    }
    // Parses a primitive name token (case-insensitive). Internal — shared with the add verb.
    private static bool TryParsePrimitive(ReadOnlySpan<char> token, out AvatarPrimitive type) {
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
    private CommandResult UngroupHandler(CommandContext context, WireArgs args) {
        var (slot, model, error) = ResolveBench(
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

        return Echo(
            detail: $"{released} shapes released to plain Union",
            slot: slot,
            verb: "editor.sculpt.ungroup"
        );
    }
    private static CommandResult Usage(string verb, string form) =>
        CommandResult.Error(output: $"[{verb}: expected {form}]");
    private CommandResult ZoomHandler(CommandContext context, WireArgs args) {
        if (EditorCommandModule.TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "out",
            positive: "in"
        )) {
            var (slot, error) = EditorCommandModule.ResolveSlot(
                context: context,
                args: in args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: ZoomCommand
            );

            if (error is { } resolveError) {
                return resolveError;
            }

            if (BenchGuard(
                slot: slot,
                verb: ZoomCommand
            ) is { } guard) {
                return guard;
            }

            var stepped = m_session.StepOrbitDistance(
                slot: slot,
                zoomIn: (direction > 0)
            );

            return Echo(
                slot: slot,
                verb: ZoomCommand,
                detail: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"orbit {stepped:0.##} u"
                )
            );
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.sculpt.zoom: expected <in|out|distance> plus an optional seat 1..4]");
        }

        var (valueSlot, valueError) = EditorCommandModule.ResolveSlot(
            args: in args,
            at: 1,
            context: context,
            verb: ZoomCommand
        );

        if (valueError is { } resolveValueError) {
            return resolveValueError;
        }

        if (BenchGuard(
            slot: valueSlot,
            verb: ZoomCommand
        ) is { } valueGuard) {
            return valueGuard;
        }

        if (!EditorCommandModule.TryFloat(
            args: in args,
            at: 0,
            value: out var distance
        )) {
            return CommandResult.Error(output: "[editor.sculpt.zoom: expected in, out, or a finite distance]");
        }

        var applied = m_session.SetOrbitDistance(
            distance: distance,
            slot: valueSlot
        );

        return Echo(
            slot: valueSlot,
            verb: ZoomCommand,
            detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"orbit {applied:0.##} u"
            )
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.new",
            description: "Opens a seat's sculpt workbench on a BLANK model authoring toward a new creation row: editor.sculpt.new <rowId> [<x> <y> <z>] [seat]. The bench anchors at the given world position (default: the seat's editor focus); the live preview stamps there through the SAME canonical geometry a committed placement uses; the seat's binding group flips to the sculpt pages and the camera orbits the bench. Requires editor mode; a rowId matching an existing creation row rejects (editor.sculpt.edit loads it).",
            handler: NewHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.edit",
            description: "Opens a seat's sculpt workbench on an EXISTING creation row: editor.sculpt.edit <rowId> [<x> <y> <z>] [seat]. The row's document loads into the model (carried cameras/behavior/text-runs/extensions ride along untouched); commit upserts the same row, and live placements of it refresh on delivery.",
            handler: EditHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ExitCommand,
            description: "Closes a seat's sculpt workbench, DISCARDING uncommitted edits and the local ring (commit first to keep the work): editor.sculpt.exit [seat]. The binding group flips back to the editor pages. The chord twin is Back on the sculpt page (or the sculpt wheel's Done sector).",
            handler: ExitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CommitCommand,
            description: "Commits the seat's sculpt: canonicalize (validate + normalize + hash) and submit ONE UpsertCreation carrying doc + hash from the same canonical pipeline: editor.sculpt.commit [seat]. The world journal gains exactly one entry (world.undo reverts it — the POST-commit undo domain; mid-sculpt undo is editor.sculpt.undo's local ring). Live placements of the row refresh on delivery; an animated row restarts its replay through the hash-diff release+recreate. The chord twin is North on the LT bench page.",
            handler: CommitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: EaselCommand,
            description: "Authors the diegetic preview EASEL beside the seat's workbench: editor.sculpt.easel [screenIndex] [seat] — upserts a fixed camera ('easel-<seat>') framing the bench and re-points an existing screen row (default: the first declared screen) at its feed, moved beside the bench. Two ordinary mutations through the live camera/screen reconcile. world.undo twice retires it.",
            handler: EaselHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.status",
            description: "Echoes a seat's sculpt state: editor.sculpt.status [seat] — row id, stamp-shape budget, selection target, timeline cursor, chain count, local-ring depth, and uncommitted-edit count.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: UndoCommand,
            description: "Steps the seat's LOCAL sculpt ring back one edit (the mid-sculpt undo domain — the world journal is untouched; post-commit undo is world.undo): editor.sculpt.undo [seat]. The chord twin is West on the sculpt page.",
            handler: (context, args) => RingHandler(
                args: in args,
                context: context,
                redo: false,
                verb: UndoCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: RedoCommand,
            description: "Steps the seat's LOCAL sculpt ring forward one edit: editor.sculpt.redo [seat]. The chord twin is East on the sculpt page.",
            handler: (context, args) => RingHandler(
                args: in args,
                context: context,
                redo: true,
                verb: RedoCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ZoomCommand,
            description: "Sets or steps the seat's workbench orbit distance: editor.sculpt.zoom <in|out|distance> [seat] (clamped 1.5..60). 'in'/'out' step (the typed twins of the LT bench page's D-pad Up/Down chord).",
            handler: ZoomHandler,
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: AddCommand,
            description: "Adds a shape to the seat's sculpt and selects it: editor.sculpt.add [primitive] [<x> <y> <z>] [seat] — sphere|box|torus|cylinder|capsule|ellipsoid|roundcone (default: the brush's primitive) at workbench-local coordinates (default: the spawn point). The new shape inherits the brush's style and the brush's palette slot advances. The chord twin is South on the sculpt page.",
            handler: AddHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: RemoveCommand,
            description: "Bare: deletes the SELECTED shape (the selection clears): editor.sculpt.remove [seat] — the chord twin is D-pad Down on the sculpt page. With a path: removes ANY document row by array index: editor.sculpt.remove shapes[3] (also chains[n], frames[n], textRuns[n] — acts on the invoking seat's own bench, no trailing seat token).",
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
            description: "Selects a sculpt shape by id or name (editor.sculpt.select <id|name> [seat]), OR folds the target cycle onto the same verb: editor.sculpt.select [next|prev] [seat] — forward/backward through the shapes, THEN the chain goals, wrapping through none/brush. Edit verbs then act on the target; editor.sculpt.deselect reverts it to the brush.",
            handler: SelectHandler,
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
            description: "Cycles the TARGET's primitive: editor.sculpt.primitive [next|prev] [seat] (default next — the chord twin is North on the sculpt page). On the brush it changes what the next add draws. editor.sculpt.set .type \"<name>\" sets it directly.",
            handler: PrimitiveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ScaleCommand,
            description: "Steps the TARGET's scale ~15% (uniform, clamped 0.2..3): editor.sculpt.scale <grow|shrink> [seat] — the typed twins of the style page's Grow/Shrink D-pad chord. editor.sculpt.set .scale [x,y,z] sets it directly; the continuous stick-driven scale gesture is unaffected.",
            handler: ScaleHandler,
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.set",
            description: "Sets ANY document member by dotted/indexed PATH — the creation-scoped twin of world.row.set: editor.sculpt.set <path> <json>. <path> is the document's own camelCase member path (shapes[3].scale, palette[0].color, textRuns[0].text, name), or .<field> for the current target (the selected shape, a targeted chain goal, or a flat brush field when nothing is selected). A bare list path (shapes, palette) with a JSON ARRAY payload replaces the whole list; with a JSON OBJECT payload it upserts one row (by id for shapes/chains, by name for frames, else appended). Validated through the same canonicalizer the load path prints; a refused edit leaves the document untouched. Acts on the invoking seat's own bench (no trailing seat token — the JSON tail is free text).",
            handler: SetHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: BlendCommand,
            description: "Cycles the TARGET's blend op (a non-Union blend coerces an ungrouped shape into its own group-of-one): editor.sculpt.blend [next|prev] [seat] (default next — the chord twin is South on the RT style page). editor.sculpt.set .blend \"<op>\" sets it directly.",
            handler: BlendHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MaterialCommand,
            description: "Cycles the TARGET's palette slot (0..15): editor.sculpt.material <next|prev> [seat] (the typed twins of the style page's Color+/- D-pad chord). editor.sculpt.set .material <slot> sets it directly.",
            handler: MaterialHandler,
            valueKind: CommandValueKind.Axis1D
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
