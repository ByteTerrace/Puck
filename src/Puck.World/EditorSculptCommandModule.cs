using System.Globalization;
using System.Numerics;
using Puck.Authoring;
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
    /// <summary>The bench-exit act (Back/Tab on the sculpt resting page).</summary>
    public const string ExitCommand = "editor.sculpt.exit";
    /// <summary>The local-ring undo act (West on the sculpt resting page).</summary>
    public const string UndoCommand = "editor.sculpt.undo";
    /// <summary>The local-ring redo act (East on the sculpt resting page).</summary>
    public const string RedoCommand = "editor.sculpt.redo";
    /// <summary>The commit act (North on the LT bench page): one canonicalized UpsertCreation.</summary>
    public const string CommitCommand = "editor.sculpt.commit";
    /// <summary>The easel act (South on the LT bench page): the diegetic preview screen + camera pair.</summary>
    public const string EaselCommand = "editor.sculpt.easel";
    /// <summary>The zoom-in chord act (D-pad Up on the LT bench page).</summary>
    public const string ZoomInCommand = "editor.sculpt.zoom.in";
    /// <summary>The zoom-out chord act (D-pad Down on the LT bench page).</summary>
    public const string ZoomOutCommand = "editor.sculpt.zoom.out";

    // The easel's fixed vantage/screen offsets from the workbench origin — one deliberate diagonal that frames the
    // bench envelope (pivot lift 1, model bound ±6) inside the offscreen view, and a slab spot beside the bench
    // that never occludes that vantage. Contract-shaped placement, not tuning: the proof pins the echoes, and a
    // world remains free to re-pose both rows afterward (they are ordinary camera/screen rows).
    private static readonly Vector3 s_easelEyeOffset = new(x: 2.6f, y: 2.2f, z: 3.6f);
    private static readonly Vector3 s_easelLookOffset = new(x: 0f, y: 1f, z: 0f);
    private static readonly Vector3 s_easelScreenOffset = new(x: -2.6f, y: 1.5f, z: 1.4f);
    private const uint EaselRenderWidth = 320;
    private const uint EaselRenderHeight = 240;
    private const float EaselFieldOfViewRadians = 0.9f;

    private readonly WorldEditorSession m_session = session;
    private readonly WorldWorkbench m_workbench = workbench;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.new",
            description: "Opens a seat's sculpt workbench on a BLANK model authoring toward a new creation row: editor.sculpt.new <rowId> [<x> <y> <z>] [seat]. The bench anchors at the given world position (default: the seat's editor focus, dropped to the ground plane); the live preview stamps there through the SAME canonical geometry a committed placement uses; the seat's binding group flips to the sculpt pages and the camera orbits the bench. Requires editor mode; a rowId matching an existing creation row rejects (editor.sculpt.edit loads it).",
            handler: NewHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.edit",
            description: "Opens a seat's sculpt workbench on an EXISTING creation row: editor.sculpt.edit <rowId> [<x> <y> <z>] [seat]. The row's document loads into the model (carried cameras/behavior/text-runs/extensions ride along untouched); commit upserts the same row, and live placements of it refresh on delivery.",
            handler: EditHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ExitCommand,
            description: "Closes a seat's sculpt workbench, DISCARDING uncommitted edits and the local ring (commit first to keep the work): editor.sculpt.exit [seat]. The binding group flips back to the editor pages. The chord twin is Back/Tab on the sculpt page.",
            handler: ExitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: CommitCommand,
            description: "Commits the seat's sculpt: canonicalize (validate + normalize + hash) and submit ONE UpsertCreation carrying doc + hash from the same canonical pipeline: editor.sculpt.commit [seat]. The world journal gains exactly one entry (world.undo reverts it — the POST-commit undo domain; mid-sculpt undo is editor.sculpt.undo's local ring). Live placements of the row refresh on delivery; an animated row restarts its replay through the hash-diff release+recreate. The chord twin is North on the LT bench page.",
            handler: CommitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: EaselCommand,
            description: "Authors the diegetic preview EASEL beside the seat's workbench: editor.sculpt.easel [screenIndex] [seat] — upserts a fixed camera ('easel-<seat>') framing the bench and re-points an existing screen row (default: the first declared screen) at its feed, moved beside the bench. Two ordinary mutations through the live camera/screen reconcile — the screen's offscreen view renders the composed world program, sculpt preview included. world.undo twice retires it.",
            handler: EaselHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.status",
            description: "Echoes a seat's sculpt state: editor.sculpt.status [seat] — row id, stamp-shape budget, selection target, timeline cursor, chain count, local-ring depth, and uncommitted-edit count. The scripted assertion point for the bench.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: UndoCommand,
            description: "Steps the seat's LOCAL sculpt ring back one edit (the mid-sculpt undo domain — the world journal is untouched; post-commit undo is world.undo): editor.sculpt.undo [seat]. The chord twin is West on the sculpt page.",
            handler: (context, args) => RingHandler(context: context, args: args, redo: false, verb: UndoCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: RedoCommand,
            description: "Steps the seat's LOCAL sculpt ring forward one edit: editor.sculpt.redo [seat]. The chord twin is East on the sculpt page.",
            handler: (context, args) => RingHandler(context: context, args: args, redo: true, verb: RedoCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.sculpt.zoom",
            description: "Sets the seat's workbench orbit distance: editor.sculpt.zoom <in|out|distance> [seat] (clamped 1.5..60). The chord twins are D-pad Up/Down on the LT bench page.",
            handler: ZoomHandler
        );
        yield return CommandDefinition.Verb(
            name: ZoomInCommand,
            description: "Steps the sculpting seat's orbit closer (D-pad Up on the LT bench page). The typed twin is editor.sculpt.zoom.",
            valueKind: CommandValueKind.Digital,
            handler: context => ZoomStepHandler(context: context, zoomIn: true, name: ZoomInCommand)
        );
        yield return CommandDefinition.Verb(
            name: ZoomOutCommand,
            description: "Steps the sculpting seat's orbit farther (D-pad Down on the LT bench page). The typed twin is editor.sculpt.zoom.",
            valueKind: CommandValueKind.Digital,
            handler: context => ZoomStepHandler(context: context, zoomIn: false, name: ZoomOutCommand)
        );
    }

    /// <summary>Resolves the acting seat and its OPEN bench model for a sculpt verb, sharing the editor slot
    /// convention (trailing [seat] token authoritative). Internal — the sibling sculpt modules ride it.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <param name="session">The editor session (the mode guard).</param>
    /// <param name="workbench">The workbench (the bench guard).</param>
    /// <returns>The slot and model, or an error result.</returns>
    internal static (int Slot, SculptModel? Model, CommandResult? Error) ResolveBench(CommandContext context, string[] args, int at, string verb, WorldEditorSession session, WorldWorkbench workbench) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: at, verb: verb);

        if (error is { } resolveError) {
            return (Slot: slot, Model: null, Error: resolveError);
        }

        if (!session.IsEditing(slot: slot)) {
            return (Slot: slot, Model: null, Error: Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]"));
        }

        if (workbench.Model(slot: slot) is not { } model) {
            return (Slot: slot, Model: null, Error: Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt — editor.sculpt.new <rowId> or editor.sculpt.edit <rowId> first]"));
        }

        return (Slot: slot, Model: model, Error: null);
    }

    private CommandResult NewHandler(CommandContext context, string[] args) =>
        OpenHandler(context: context, args: args, verb: "editor.sculpt.new", loadExisting: false);

    private CommandResult EditHandler(CommandContext context, string[] args) =>
        OpenHandler(context: context, args: args, verb: "editor.sculpt.edit", loadExisting: true);

    // The shared bench-open flow: resolve the row id + optional explicit origin + seat, guard the mode, load or
    // refuse against the existing rows, envelope-check the composed preview, then flip the binding group and seed
    // the orbit.
    private CommandResult OpenHandler(CommandContext context, string[] args, string verb, bool loadExisting) {
        if (args.Length is (< 1 or > 5)) {
            return Error(text: $"[{verb}: expected <rowId> [<x> <y> <z>] [seat]]");
        }

        var rowId = args[0];
        var hasPosition = (args.Length >= 4);

        var x = 0f;
        var y = 0f;
        var z = 0f;

        if (hasPosition && (!EditorCommandModule.TryFloat(args: args, at: 1, value: out x) ||
            !EditorCommandModule.TryFloat(args: args, at: 2, value: out y) ||
            !EditorCommandModule.TryFloat(args: args, at: 3, value: out z))) {
            return Error(text: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: (hasPosition ? 4 : 1), verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!m_session.IsEditing(slot: slot)) {
            return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
        }

        var existing = FindCreation(id: rowId);
        CreationDocument? document = null;

        if (loadExisting) {
            if (existing is not { } row) {
                return Error(text: $"[{verb}: no creation row '{rowId}' — see editor.creations, or editor.sculpt.new {rowId} starts blank]");
            }

            document = row.Document;
        } else if (existing is not null) {
            return Error(text: $"[{verb}: creation row '{rowId}' already exists — editor.sculpt.edit {rowId} loads it, or pick a new id]");
        }

        // Default origin: the editor focus dropped to the ground plane, so the bench lands where the editor looks.
        var focus = m_session.Focus(slot: slot);
        var origin = (hasPosition ? new Vector3(x: x, y: y, z: z) : new Vector3(x: focus.X, y: 0f, z: focus.Z));

        if (!m_workbench.TryEnter(slot: slot, rowId: rowId, origin: origin, document: document, error: out var enterError)) {
            return Error(text: $"[{verb}: {enterError}]");
        }

        _ = m_seatBindings.SetActiveGroup(slot: slot, group: WorldEditorBindings.SculptGroupId);
        m_session.SeedWorkbenchOrbit(slot: slot);

        var model = m_workbench.Model(slot: slot)!;

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"sculpting '{rowId}' at ({origin.X:0.00}, {origin.Y:0.00}, {origin.Z:0.00}) — {model.StampShapeCount}/{model.ShapeCapacity} stamp shapes, group sculpt (LT bench, RT style, LT+RT frames, RT+LT rig)"
        ));
    }

    private CommandResult ExitHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: ExitCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!m_workbench.IsActive(slot: slot)) {
            return new CommandResult(Output: $"[{ExitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt]") { IsError = true };
        }

        var rowId = m_workbench.RowId(slot: slot);
        var discarded = m_workbench.UncommittedEdits(slot: slot);

        _ = m_workbench.Drop(slot: slot);
        // Back to the editor page family (the seat is still in editor mode — the bench was a mode WITHIN it).
        _ = m_seatBindings.SetActiveGroup(slot: slot, group: WorldEditorBindings.GroupId);

        return Echo(slot: slot, verb: ExitCommand, detail: $"closed '{rowId}'{((discarded > 0) ? $" ({discarded} uncommitted edits discarded)" : string.Empty)} — group editor");
    }

    private CommandResult CommitHandler(CommandContext context, string[] args) {
        var (slot, model, error) = ResolveBench(context: context, args: args, at: 0, verb: CommitCommand, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var rowId = m_workbench.RowId(slot: slot);
        CanonicalDocument<CreationDocument> canonical;

        try {
            canonical = CreationCanonicalizer.Canonicalize(document: model!.ToDocument(), source: rowId);
        } catch (DocumentValidationException exception) {
            return Error(text: $"[{CommitCommand}: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }

        // Doc + hash from the SAME canonical result — the hash-provenance contract, satisfied structurally.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: WorldPrincipal.Seat(slot: slot),
            Creation: new WorldCreation(Id: rowId, Document: canonical.Document, Hash: canonical.Hash)
        ));
        // Clean tracking follows the SERVER, not the enqueue: the bench flips clean only when the accepted row is
        // delivered (WorldWorkbench.Tick), so a rejected apply keeps the work counted as uncommitted.
        m_workbench.NoteCommitSubmitted(slot: slot, hash: canonical.Hash);

        return Echo(slot: slot, verb: CommitCommand, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"'{rowId}' sha256 {canonical.Hash[..12]}… ({canonical.Document.StampShapeCount()} stamp shapes, {(canonical.Document.Frames?.Count ?? 0)} frames) — one UpsertCreation submitted (clean on server accept); world.undo reverts it, editor.sculpt.undo stays local"
        ));
    }

    private CommandResult EaselHandler(CommandContext context, string[] args) {
        // Shapes: none = default screen + acting seat; [screenIndex] and/or trailing [seat].
        var hasIndex = ((args.Length >= 2) || ((args.Length == 1) && !SeatToken(token: args[0])));
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: (hasIndex ? 1 : 0), verb: EaselCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (BenchGuard(slot: slot, verb: EaselCommand) is { } guard) {
            return guard;
        }

        var screens = m_client.Definition.Screens;

        if (screens.Count == 0) {
            return Error(text: $"[{EaselCommand}: the world declares no screen rows — author one with world.screen.set first (runtime screens need a declared index)]");
        }

        WorldScreen? target = null;

        if (hasIndex) {
            if (!int.TryParse(s: args[0], provider: CultureInfo.InvariantCulture, result: out var index)) {
                return Error(text: $"[{EaselCommand}: could not parse screen index '{args[0]}']");
            }

            foreach (var screen in screens) {
                if (screen.Index == index) {
                    target = screen;

                    break;
                }
            }

            if (target is null) {
                return Error(text: $"[{EaselCommand}: no screen row with index {index} — see world.screens]");
            }
        } else {
            target = screens[0];
        }

        var origin = m_workbench.Origin(slot: slot);
        var cameraName = $"easel-{PlayerRoster.DisplayNumber(slot: slot)}";
        var principal = WorldPrincipal.Seat(slot: slot);

        // Two ordinary mutations: the fixed easel camera framing the bench, then the screen row moved beside it and
        // re-pointed at the camera's view — both land through the live reconcile path (no restart, no new machinery).
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCamera(
            Principal: principal,
            Camera: new WorldCamera(
                Name: cameraName,
                Anchor: null,
                Offset: (origin + s_easelEyeOffset),
                Rig: new WorldRig.LookAt(Target: (origin + s_easelLookOffset), FieldOfViewRadians: EaselFieldOfViewRadians),
                RenderWidth: EaselRenderWidth,
                RenderHeight: EaselRenderHeight
            )
        ));
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(
            Principal: principal,
            Screen: (target with {
                Origin = (origin + s_easelScreenOffset),
                Source = new WorldScreenSource.View(CameraName: cameraName),
            })
        ));

        return Echo(slot: slot, verb: EaselCommand, detail: $"camera '{cameraName}' + screen {target.Index} re-pointed at its view beside the bench — two mutations submitted (world.undo twice retires the easel)");
    }

    private CommandResult StatusHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: "editor.sculpt.status");

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

            target = string.Create(provider: CultureInfo.InvariantCulture, handler: $"target=goal chain {chain.Id} at ({chain.Goal.X:0.00}, {chain.Goal.Y:0.00}, {chain.Goal.Z:0.00})");
        } else if (model.SelectedShape is { } shape) {
            target = string.Create(provider: CultureInfo.InvariantCulture, handler: $"target=shape {shape.Id} ({shape.Type}) at ({shape.Position.X:0.00}, {shape.Position.Y:0.00}, {shape.Position.Z:0.00})");
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.sculpt.status: seat {seat} sculpting '{m_workbench.RowId(slot: slot)}' shapes {model.StampShapeCount}/{model.ShapeCapacity} {target} frame {model.CurrentFrame}/{model.FrameCount}{(model.Playing ? " playing" : string.Empty)} chains {model.Chains.Count} ring {model.HistoryCount}/{SculptModel.HistoryCapacity} uncommitted {m_workbench.UncommittedEdits(slot: slot)}{(m_workbench.IsCommitPending(slot: slot) ? " commit=pending" : string.Empty)} origin=({origin.X:0.00}, {origin.Y:0.00}, {origin.Z:0.00})]"
        ));
    }

    private CommandResult RingHandler(CommandContext context, string[] args, bool redo, string verb) {
        var (slot, model, error) = ResolveBench(context: context, args: args, at: 0, verb: verb, session: m_session, workbench: m_workbench);

        if (error is { } benchError) {
            return benchError;
        }

        var applied = (redo ? model!.Redo() : model!.Undo());

        if (!applied) {
            return new CommandResult(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} nothing to {(redo ? "redo" : "undo")} on the local ring]") { IsError = true };
        }

        return Echo(slot: slot, verb: verb, detail: $"local ring — restored ({(model.CanUndo ? "more undo available" : "at the baseline")}); world journal untouched");
    }

    private CommandResult ZoomHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.sculpt.zoom: expected <in|out|distance> plus an optional seat 1..4]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 1, verb: "editor.sculpt.zoom");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (BenchGuard(slot: slot, verb: "editor.sculpt.zoom") is { } guard) {
            return guard;
        }

        float applied;

        if (string.Equals(a: args[0], b: "in", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            applied = m_session.StepOrbitDistance(slot: slot, zoomIn: true);
        } else if (string.Equals(a: args[0], b: "out", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            applied = m_session.StepOrbitDistance(slot: slot, zoomIn: false);
        } else if (EditorCommandModule.TryFloat(args: args, at: 0, value: out var distance)) {
            applied = m_session.SetOrbitDistance(slot: slot, distance: distance);
        } else {
            return Error(text: "[editor.sculpt.zoom: expected in, out, or a finite distance]");
        }

        return Echo(slot: slot, verb: "editor.sculpt.zoom", detail: string.Create(provider: CultureInfo.InvariantCulture, handler: $"orbit {applied:0.##} u"));
    }

    private CommandResult ZoomStepHandler(CommandContext context, bool zoomIn, string name) {
        if (context.Parse is not null) {
            return new CommandResult(Output: $"[{name}: the bound zoom step — type editor.sculpt.zoom <in|out|distance> instead]") { IsError = true };
        }

        var slot = context.Slot;

        if (!m_workbench.IsActive(slot: slot)) {
            return CommandResult.None;
        }

        return Echo(slot: slot, verb: name, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"orbit {m_session.StepOrbitDistance(slot: slot, zoomIn: zoomIn):0.##} u"
        ));
    }

    // The open-bench guard for verbs that resolved their seat separately (an explicit trailing token stays honored).
    private CommandResult? BenchGuard(int slot, string verb) {
        if (!m_session.IsEditing(slot: slot)) {
            return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
        }

        if (!m_workbench.IsActive(slot: slot)) {
            return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no open sculpt — editor.sculpt.new <rowId> first]");
        }

        return null;
    }

    private WorldCreation? FindCreation(string id) {
        foreach (var creation in m_client.Definition.Creations) {
            if (string.Equals(a: creation.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return creation;
            }
        }

        return null;
    }

    // Whether a lone token reads as a seat number (1..4) — the easel's [screenIndex]-vs-[seat] discriminator.
    private static bool SeatToken(string token) =>
        (int.TryParse(s: token, provider: CultureInfo.InvariantCulture, result: out var value) && (value is >= 1 and <= PlayerRoster.MaxSlots));

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
