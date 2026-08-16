using System.Globalization;
using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The sculpt workbench's lifecycle console surface — the assist-layer twins of the sculpt group's deliberate
/// chords. <c>editor.sculpt.new</c>/<c>edit</c> open a seat's bench (a blank model, or an existing creation row
/// loaded through the canonical pipeline) and flip its active binding group onto the sculpt pages;
/// <c>editor.sculpt.commit</c> canonicalizes the model and submits ONE <c>UpsertCreation</c> (doc + hash always come
/// from the same canonicalize call; live placements of the row refresh on delivery,
/// animated ones through the animator's hash-diff release+recreate); <c>editor.sculpt.easel</c> authors the diegetic
/// preview easel (a fixed workbench camera + an existing screen row re-pointed at its feed — two ordinary mutations,
/// zero engine change); <c>editor.sculpt.undo</c>/<c>redo</c> walk the LOCAL ring (the world journal is untouched —
/// the two undo domains narrate distinctly). A SEPARATE module per concern to keep every class under its analyzer
/// ceilings (shape/style/rig verbs live in their sibling modules).
/// </summary>
/// <remarks><c>new</c>/<c>edit</c>/<c>exit</c>/<c>commit</c>/<c>easel</c> route Simulation (they follow a
/// sim-routed <c>editor.enter</c> in a scripted burst, and commit/easel submit mutations the stdin barrier then
/// serializes reads behind); the ring/zoom/status verbs are pure client state and stay Immediate.</remarks>
internal sealed class EditorSculptCommandModule(WorldEditorSession session, WorldWorkbench workbench, WorldSeatBindings seatBindings, WorldClient client, IServerLink link) : ICommandModule {
    private const float EaselFieldOfViewRadians = 0.9f;
    private const uint EaselRenderHeight = 240;
    private const uint EaselRenderWidth = 320;

    /// <summary>The commit act (North on the LT bench page): one canonicalized UpsertCreation.</summary>
    public const string CommitCommand = "editor.sculpt.commit";
    /// <summary>The easel act (South on the LT bench page): the diegetic preview screen + camera pair.</summary>
    public const string EaselCommand = "editor.sculpt.easel";
    /// <summary>The bench-exit act (Back on the sculpt resting page; the sculpt wheel's Done sector).</summary>
    public const string ExitCommand = "editor.sculpt.exit";
    /// <summary>The local-ring redo act (East on the sculpt resting page).</summary>
    public const string RedoCommand = "editor.sculpt.redo";
    /// <summary>The local-ring undo act (West on the sculpt resting page).</summary>
    public const string UndoCommand = "editor.sculpt.undo";
    /// <summary>The zoom verb: <c>editor.sculpt.zoom &lt;in|out|distance&gt;</c>. D-pad Up/Down on the LT bench page
    /// bind it with a constant Axis1D value (+1 in, -1 out) in place of an argument.</summary>
    public const string ZoomCommand = "editor.sculpt.zoom";

    // The easel's fixed vantage/screen offsets from the workbench origin — one deliberate diagonal that frames the
    // bench envelope (pivot lift 1, model bound ±6) inside the offscreen view, and a slab spot beside the bench
    // that never occludes that vantage. Contract-shaped placement, not tuning: the proof pins the echoes, and a
    // world remains free to re-pose both rows afterward (they are ordinary camera/screen rows).
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
    /// convention (trailing [seat] token authoritative). Internal — the sibling sculpt modules ride it.</summary>
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
    // Whether a lone token reads as a seat number (1..4) — the easel's [screenIndex]-vs-[seat] discriminator, shared
    // with the rig module's own [target]-vs-[seat] arity guesses.
    internal static bool SeatToken(ReadOnlySpan<char> token) =>
        (int.TryParse(
            s: token,
            provider: CultureInfo.InvariantCulture,
            result: out var value
        ) && (value is >= 1 and <= PlayerRoster.MaxSlots));

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
                document: model!.ToDocument(),
                source: rowId
            );
        } catch (DocumentValidationException exception) {
            return CommandResult.Error(output: $"[{CommitCommand}: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }

        // Doc + hash from the SAME canonical result — the hash-provenance contract, satisfied structurally.
        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: context.ActingPrincipal(),
            Creation: new WorldCreation(
                Id: rowId,
                Document: canonical.Document,
                Hash: canonical.Hash
            )
        ));
        // Clean tracking follows the SERVER, not the enqueue: the bench flips clean only when the accepted row is
        // delivered (WorldWorkbench.Tick), so a rejected apply keeps the work counted as uncommitted.
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
        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        var principal = context.ActingPrincipal();

        // Two ordinary mutations: the fixed easel camera framing the bench, then the screen row moved beside it and
        // re-pointed at the camera's view — both land through the live reconcile path (no restart, no new machinery).
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
        // Back to the editor page family (the seat is still in editor mode — the bench was a mode WITHIN it). The
        // bench IS closed either way: refusing the exit over a missing group would strand the seat on a sculpt page
        // whose bench no longer exists, which is strictly worse than exiting onto the wrong page and saying so.
        var regrouped = m_seatBindings.SetActiveGroup(
            group: WorldEditorBindings.GroupId,
            slot: slot
        );
        var groupNote = (regrouped
            ? " — group editor"
            : $" — WARNING: this seat's profile declares no '{WorldEditorBindings.GroupId}' binding group, so the seat keeps its current page"
        );

        return Echo(
            detail: $"closed '{rowId}'{((discarded > 0)
            ? $" ({discarded} uncommitted edits discarded)"
            : string.Empty)}{groupNote}",
            slot: slot,
            verb: ExitCommand
        );
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

        // The group flip is a precondition of the bench, not a decoration on it: without the sculpt page every sculpt
        // verb resolves against whatever page the seat is already on. The bench is dropped rather than left open,
        // because a half-entered mode is worse than a refused one.
        if (!m_seatBindings.SetActiveGroup(
            group: WorldEditorBindings.SculptGroupId,
            slot: slot
        )) {
            _ = m_workbench.Drop(slot: slot);

            return CommandResult.Error(output: $"[{verb}: this seat's profile declares no '{WorldEditorBindings.SculptGroupId}' binding group, so the bench has no page to resolve its verbs against — the bench was not opened]");
        }

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

        if (model.TargetIsGoal) {
            var chain = model.TargetGoalChain!;

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
    // The zoom fold: a leading in|out literal, or (with no token) a bound constant Axis1D value from the LT bench
    // page's D-pad Up/Down step chord — see EditorCommandModule.TryDirection. Anything else falls through to the
    // original <distance> form.
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
            description: "Authors the diegetic preview EASEL beside the seat's workbench: editor.sculpt.easel [screenIndex] [seat] — upserts a fixed camera ('easel-<seat>') framing the bench and re-points an existing screen row (default: the first declared screen) at its feed, moved beside the bench. Two ordinary mutations through the live camera/screen reconcile — the screen's offscreen view renders the composed world program, sculpt preview included. world.undo twice retires it.",
            handler: EaselHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.sculpt.status",
            description: "Echoes a seat's sculpt state: editor.sculpt.status [seat] — row id, stamp-shape budget, selection target, timeline cursor, chain count, local-ring depth, and uncommitted-edit count. The scripted assertion point for the bench.",
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
            description: "Sets or steps the seat's workbench orbit distance: editor.sculpt.zoom <in|out|distance> [seat] (clamped 1.5..60). 'in'/'out' step (the typed twins of the LT bench page's D-pad Up/Down chord, which binds this same verb with no argument, direction from the binding's constant value).",
            handler: ZoomHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared here so
            // BindingVocabularyCheck admits the row instead of rejecting every future recompose that touches this
            // seat. See CommandDefinition.WithWireArgs's doctrine comment.
            valueKind: CommandValueKind.Axis1D
        );
    }
}
