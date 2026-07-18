using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The selection-and-manipulation console surface — the typed twin of every P3 chord act (§chord-first / the
/// game-studio numeric-entry demand). Targeting verbs (<c>editor.select</c>/<c>pick</c>/<c>next</c>/<c>prev</c>/
/// <c>deselect</c>) act on the client-local selection; the drag verbs (<c>editor.grab</c>/<c>drag</c>/<c>release</c>/
/// <c>cancel</c>/<c>spawn.*</c>) drive the pending-row preview channel and commit ONE whole-row mutation on the
/// release edge; the discrete verbs (<c>editor.move</c>/<c>nudge</c>/<c>place</c>/<c>delete</c>) submit an immediate
/// whole-row mutation per act. Mutations carry the ACTING SEAT principal, so grant denials land on the seat that
/// asked. A SEPARATE module from <see cref="EditorCommandModule"/> to keep every class under its analyzer ceilings.
/// </summary>
/// <remarks>Verbs that submit a mutation route Simulation (the stdin barrier then serializes a following
/// <c>world.status</c>/<c>editor.status</c> read-after-write); pure client-state verbs stay Immediate — including
/// <c>editor.drag</c>, whose motion never crosses the wire (that is the whole point of the channel).</remarks>
internal sealed class EditorSelectionCommandModule(WorldEditorSession session, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldClient client, IServerLink link) : ICommandModule {
    /// <summary>The crosshair pick act (South on the select page; North on the camera page — the focus-selection).</summary>
    public const string PickCommand = "editor.pick";
    /// <summary>The cycle-next act (D-pad Right on the select page): the next proximity candidate, nearest-first.</summary>
    public const string NextCommand = "editor.next";
    /// <summary>The cycle-previous act (D-pad Left on the select page).</summary>
    public const string PrevCommand = "editor.prev";
    /// <summary>The deselect act (West on the select page).</summary>
    public const string DeselectCommand = "editor.deselect";
    /// <summary>The delete-selected act (East on the select page) — a discrete whole-row remove mutation.</summary>
    public const string DeleteCommand = "editor.delete";
    /// <summary>The grab/release toggle (South on the place page; North on the select page): begins a drag on the
    /// selection, or commits a live drag as one mutation.</summary>
    public const string GrabCommand = "editor.grab";
    /// <summary>The explicit release (typed scripting twin of the grab toggle's commit edge).</summary>
    public const string ReleaseCommand = "editor.release";
    /// <summary>The drag abort (East on the place page): the pending row never existed.</summary>
    public const string CancelCommand = "editor.cancel";
    /// <summary>The snap toggle (West on the place page) and pitch setter.</summary>
    public const string SnapCommand = "editor.snap";
    /// <summary>The new-boulder ghost act (D-pad Up on the place page).</summary>
    public const string SpawnBoulderCommand = "editor.spawn.boulder";
    /// <summary>The new-slab ghost act (D-pad Down on the place page).</summary>
    public const string SpawnSlabCommand = "editor.spawn.slab";

    // The default authored dimensions a chord-spawned/verb-defaulted row carries (typed args override).
    private const float DefaultBoulderRadius = 0.6f;
    private const float DefaultBoulderSmooth = 0.4f;
    private static readonly Vector3 s_defaultSlabHalfExtents = new(x: 1.5f, y: 0.25f, z: 1.5f);
    private const float DefaultSlabRound = 0.05f;
    private const float DefaultSlabSmooth = 0.3f;
    private static readonly Vector3 s_defaultSlabAlbedo = new(x: 0.62f, y: 0.56f, z: 0.45f);

    private readonly WorldEditorSession m_session = session;
    private readonly WorldEditorTargeting m_targeting = targeting;
    private readonly WorldEditorDrag m_drag = drag;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.select",
            description: "Selects a document row explicitly: editor.select <scene|screens|spawns|cameras> <id-or-index> [seat]. Screens key by engine index; every other section by its stable string id. Selection is client state (never protocol); the selected scene row tints in the render.",
            handler: SelectHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PickCommand,
            description: "Picks the row under the editor camera's crosshair via the document-derived fixed-point picking ray: editor.pick [seat]. Scene rows and screens pick by their real geometry; spawns and fixed cameras by small proxy spheres. The chord twins are South on the RT select page and North on the LT camera page.",
            handler: PickHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: NextCommand,
            description: "Cycles the selection to the next proximity candidate around the editor focus point (nearest-first, wraps; the ring is BOUNDED — the nearest 16 rows within 32u): editor.next [seat]. The chord twin is D-pad Right on the select page.",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: 1, verb: NextCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PrevCommand,
            description: "Cycles the selection to the previous proximity candidate: editor.prev [seat]. The chord twin is D-pad Left on the select page.",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: -1, verb: PrevCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: DeselectCommand,
            description: "Clears the seat's selection: editor.deselect [seat]. The chord twin is West on the select page.",
            handler: DeselectHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: DeleteCommand,
            description: "Deletes the selected row as one whole-row mutation: editor.delete [seat] — RemoveSceneRow / RemoveScreen / RemoveCamera by section; a spawn delete resends the spawn list minus the row (and rejects loudly when the local seats then lack spawns). The chord twin is East on the select page.",
            handler: DeleteHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: GrabCommand,
            description: "Toggles the drag channel on the selection: editor.grab [seat] begins a client-local drag (sticks move the pending row; NOTHING crosses the wire), and a second grab commits it as ONE whole-row mutation. Scene rows and screens drag; move spawns/cameras with editor.move. The chord twins are South on the place page and North on the select page.",
            handler: GrabHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ReleaseCommand,
            description: "Commits the live drag as ONE whole-row mutation (the release edge — a whole drag is one journal entry, one undo step): editor.release [seat].",
            handler: ReleaseHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: CancelCommand,
            description: "Aborts the live drag — the pending row never existed (no mutation, no journal entry): editor.cancel [seat]. The chord twin is East on the place page.",
            handler: CancelHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.drag",
            description: "Moves the live drag's pending row by a world-space delta — the typed twin of stick drag motion, client-local only: editor.drag <dx> <dy> <dz> [seat]. Snap applies; commit with editor.release.",
            handler: DragHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.move",
            description: "Moves the selected row to an ABSOLUTE position as one whole-row mutation: editor.move <x> <y> <z> [seat]. Scene rows move their center, screens their face origin, spawns their position; a fixed camera moves its eye (aim held), an anchored camera sets its attachment offset.",
            handler: (context, args) => MoveHandler(context: context, args: args, relative: false, verb: "editor.move"),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.nudge",
            description: "Moves the selected row by a RELATIVE delta as one whole-row mutation: editor.nudge <dx> <dy> <dz> [seat]. A fixed camera translates eye and aim together; an anchored camera shifts its attachment offset.",
            handler: (context, args) => MoveHandler(context: context, args: args, relative: true, verb: "editor.nudge"),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.place",
            description: "Places a NEW scene row at the editor focus point as one mutation (the immediate typed twin of the spawn-ghost chords): editor.place boulder [radius [smooth]] | slab [hx hy hz]. Acts for seat 1 when typed; ids allocate as boulder-N/slab-N.",
            handler: PlaceHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: SpawnBoulderCommand,
            description: "Begins a new-boulder GHOST drag at the editor focus point (previewed client-side; enters the document only on release): editor.spawn.boulder [seat]. The chord twin is D-pad Up on the place page.",
            handler: (context, args) => SpawnHandler(context: context, args: args, slab: false, verb: SpawnBoulderCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: SpawnSlabCommand,
            description: "Begins a new-slab GHOST drag at the editor focus point: editor.spawn.slab [seat]. The chord twin is D-pad Down on the place page.",
            handler: (context, args) => SpawnHandler(context: context, args: args, slab: true, verb: SpawnSlabCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: SnapCommand,
            description: "Grid snap for drags: editor.snap [on|off|<pitch>] [seat]. No argument toggles (the West place-page chord); a pitch sets the X/Z lattice (Y stays free — floor-rest placement) and enables snapping.",
            handler: SnapHandler
        );
    }

    private CommandResult SelectHandler(CommandContext context, string[] args) {
        if (args.Length is (< 2 or > 3)) {
            return Error(text: "[editor.select: expected <scene|screens|spawns|cameras> <id-or-index> [seat]]");
        }

        var section = ParseSection(token: args[0]);

        if (section is not { } resolvedSection) {
            return Error(text: $"[editor.select: unknown section '{args[0]}' — scene|screens|spawns|cameras]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 2, verb: "editor.select");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: "editor.select") is { } guard) {
            return guard;
        }

        if (!m_targeting.TrySelect(slot: slot, section: resolvedSection, key: args[1], selection: out var selection, error: out var reason)) {
            return Error(text: $"[editor.select: {reason}]");
        }

        return Echo(slot: slot, verb: "editor.select", detail: DescribeSelection(slot: slot, selection: in selection));
    }

    private CommandResult PickHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: PickCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: PickCommand) is { } guard) {
            return guard;
        }

        if (!m_targeting.TryPick(slot: slot, selection: out var selection)) {
            return Echo(slot: slot, verb: PickCommand, detail: "nothing under the crosshair");
        }

        return Echo(slot: slot, verb: PickCommand, detail: $"selected {DescribeSelection(slot: slot, selection: in selection)}");
    }

    private CommandResult CycleHandler(CommandContext context, string[] args, int direction, string verb) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: verb) is { } guard) {
            return guard;
        }

        if (m_targeting.Cycle(slot: slot, direction: direction) is not { } cycled) {
            return Echo(slot: slot, verb: verb, detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"no candidates within {WorldEditorTargeting.CandidateRadius:0}u — fly closer, or editor.select by id"
            ));
        }

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{cycled.Selection.Describe()} {cycled.Distance:0.0}u of {cycled.Count} candidates (r {WorldEditorTargeting.CandidateRadius:0}u, cap {WorldEditorTargeting.CandidateCap})"
        ));
    }

    private CommandResult DeselectHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: DeselectCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: DeselectCommand) is { } guard) {
            return guard;
        }

        return Echo(slot: slot, verb: DeselectCommand, detail: (m_targeting.Deselect(slot: slot) ? "cleared" : "nothing selected"));
    }

    private CommandResult DeleteHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: DeleteCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: DeleteCommand) is { } guard) {
            return guard;
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return Error(text: $"[{DeleteCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection]");
        }

        var principal = WorldPrincipal.Seat(slot: slot);

        switch (selection.Section) {
            case WorldSection.Scene:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveSceneRow(Principal: principal, Id: selection.Id));

                break;
            case WorldSection.Screens:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveScreen(Principal: principal, Index: selection.Index));

                break;
            case WorldSection.Cameras:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveCamera(Principal: principal, Name: selection.Id));

                break;
            case WorldSection.Spawns: {
                var spawns = new List<WorldSpawnPoint>();

                foreach (var spawn in m_client.Definition.SpawnPoints) {
                    if (!string.Equals(a: spawn.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        spawns.Add(item: spawn);
                    }
                }

                m_link.SubmitWorldMutation(mutation: new WorldMutation.SetSpawns(Principal: principal, Spawns: spawns));

                break;
            }
            default:
                return Error(text: $"[{DeleteCommand}: {selection.Describe()} has no remove mutation]");
        }

        return Echo(slot: slot, verb: DeleteCommand, detail: $"{selection.Describe()} — remove submitted");
    }

    private CommandResult GrabHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: GrabCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: GrabCommand) is { } guard) {
            return guard;
        }

        // The toggle: a live drag commits (the pad's one-button grab→move→commit flow); otherwise a grab begins.
        if (m_drag.IsDragging(slot: slot)) {
            return ReleaseCore(slot: slot, verb: GrabCommand);
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return Error(text: $"[{GrabCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection — editor.pick or editor.next first]");
        }

        if (!m_drag.TryGrab(slot: slot, selection: in selection, error: out var reason)) {
            return Error(text: $"[{GrabCommand}: {reason}]");
        }

        return Echo(slot: slot, verb: GrabCommand, detail: $"dragging {selection.Describe()} — sticks move it, grab again commits, {CancelCommand} aborts");
    }

    private CommandResult ReleaseHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: ReleaseCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: ReleaseCommand) is { } guard) {
            return guard;
        }

        return ReleaseCore(slot: slot, verb: ReleaseCommand);
    }

    private CommandResult ReleaseCore(int slot, string verb) {
        if (m_drag.Release(slot: slot, principal: WorldPrincipal.Seat(slot: slot)) is not { } echo) {
            return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag]");
        }

        return Echo(slot: slot, verb: verb, detail: echo);
    }

    private CommandResult CancelHandler(CommandContext context, string[] args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: CancelCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: CancelCommand) is { } guard) {
            return guard;
        }

        if (m_drag.Cancel(slot: slot) is not { } echo) {
            return Error(text: $"[{CancelCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag]");
        }

        return Echo(slot: slot, verb: CancelCommand, detail: echo);
    }

    private CommandResult DragHandler(CommandContext context, string[] args) {
        if (args.Length is (< 3 or > 4)) {
            return Error(text: "[editor.drag: expected <dx> <dy> <dz> [seat]]");
        }

        if (!TryVector(args: args, at: 0, value: out var delta)) {
            return Error(text: "[editor.drag: could not parse <dx> <dy> <dz> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 3, verb: "editor.drag");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: "editor.drag") is { } guard) {
            return guard;
        }

        if (!m_drag.IsDragging(slot: slot)) {
            return Error(text: $"[editor.drag: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag — editor.grab first]");
        }

        m_drag.Move(slot: slot, delta: delta);

        return Echo(slot: slot, verb: "editor.drag", detail: (m_drag.Describe(slot: slot) ?? "moved"));
    }

    private CommandResult MoveHandler(CommandContext context, string[] args, bool relative, string verb) {
        if (args.Length is (< 3 or > 4)) {
            return Error(text: $"[{verb}: expected <x> <y> <z> [seat]]");
        }

        if (!TryVector(args: args, at: 0, value: out var value)) {
            return Error(text: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 3, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: verb) is { } guard) {
            return guard;
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection]");
        }

        if (!TrySubmitMove(slot: slot, selection: in selection, value: value, relative: relative, target: out var target, reason: out var reason)) {
            return Error(text: $"[{verb}: {reason}]");
        }

        return Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{selection.Describe()} -> ({target.X:0.00}, {target.Y:0.00}, {target.Z:0.00}) — one mutation submitted"
        ));
    }

    // Compose the selected row's whole-row move mutation. The delta semantics per section are documented on the verbs.
    private bool TrySubmitMove(int slot, in EditorSelection selection, Vector3 value, bool relative, out Vector3 target, out string reason) {
        target = default;
        reason = string.Empty;

        var definition = m_client.Definition;
        var principal = WorldPrincipal.Seat(slot: slot);

        switch (selection.Section) {
            case WorldSection.Scene:
                foreach (var row in definition.Scene.Rows) {
                    if (string.Equals(a: row.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        target = (relative ? (row.Center + value) : value);
                        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSceneRow(Principal: principal, Row: row.WithCenter(center: target)));

                        return true;
                    }
                }

                break;
            case WorldSection.Screens:
                foreach (var screen in definition.Screens) {
                    if (screen.Index == selection.Index) {
                        target = (relative ? (screen.Origin + value) : value);
                        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: (screen with { Origin = target })));

                        return true;
                    }
                }

                break;
            case WorldSection.Spawns: {
                var spawns = new List<WorldSpawnPoint>(capacity: definition.SpawnPoints.Count);
                var found = false;

                foreach (var spawn in definition.SpawnPoints) {
                    if (string.Equals(a: spawn.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        target = (relative ? (spawn.Position + value) : value);
                        spawns.Add(item: (spawn with { Position = target }));
                        found = true;
                    } else {
                        spawns.Add(item: spawn);
                    }
                }

                if (found) {
                    m_link.SubmitWorldMutation(mutation: new WorldMutation.SetSpawns(Principal: principal, Spawns: spawns));

                    return true;
                }

                break;
            }
            case WorldSection.Cameras:
                foreach (var camera in definition.Cameras) {
                    if (!string.Equals(a: camera.Name, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }

                    switch (camera) {
                        case WorldCamera.Fixed fixedCamera:
                            target = (relative ? (fixedCamera.Position + value) : value);
                            // A relative nudge carries the aim along (a parallel translate); an absolute move re-poses
                            // the eye and holds the authored aim point.
                            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCamera(Principal: principal, Camera: (fixedCamera with {
                                Position = target,
                                LookAt = (relative ? (fixedCamera.LookAt + value) : fixedCamera.LookAt),
                            })));

                            return true;
                        case WorldCamera.Anchored anchored:
                            target = (relative ? (anchored.Offset + value) : value);
                            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCamera(Principal: principal, Camera: (anchored with { Offset = target })));

                            return true;
                    }
                }

                break;
        }

        reason = $"no {selection.Describe()} in the live definition";

        return false;
    }

    private CommandResult PlaceHandler(CommandContext context, string[] args) {
        if (args.Length == 0) {
            return Error(text: "[editor.place: expected boulder [radius [smooth]] | slab [hx hy hz]]");
        }

        var slot = ((context.Parse is null) ? context.Slot : 0);

        if (Guard(slot: slot, verb: "editor.place") is { } guard) {
            return guard;
        }

        var row = BuildRow(args: args, focus: m_session.Focus(slot: slot), reason: out var reason);

        if (row is null) {
            return Error(text: $"[editor.place: {reason}]");
        }

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSceneRow(Principal: WorldPrincipal.Seat(slot: slot), Row: row));

        return Echo(slot: slot, verb: "editor.place", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"scene '{row.Id}' at ({row.Center.X:0.00}, {row.Center.Y:0.00}, {row.Center.Z:0.00}) — one mutation submitted"
        ));
    }

    // Parse the place shape: `boulder [radius [smooth]]` or `slab [hx hy hz]`, defaults per the constants above.
    private WorldSceneRow? BuildRow(string[] args, Vector3 focus, out string reason) {
        reason = string.Empty;

        switch (args[0].ToUpperInvariant()) {
            case "BOULDER": {
                var radius = DefaultBoulderRadius;
                var smooth = DefaultBoulderSmooth;

                if ((args.Length >= 2) && !TryFloat(token: args[1], value: out radius)) {
                    reason = $"bad radius '{args[1]}'";

                    return null;
                }

                if ((args.Length >= 3) && !TryFloat(token: args[2], value: out smooth)) {
                    reason = $"bad smooth '{args[2]}'";

                    return null;
                }

                return new WorldSceneRow.Boulder(Id: m_drag.NextFreeSceneRowId(prefix: "boulder-"), Center: focus, Radius: radius, Smooth: smooth);
            }
            case "SLAB": {
                var halfExtents = s_defaultSlabHalfExtents;

                if (args.Length >= 4) {
                    if (!TryVector(args: args, at: 1, value: out halfExtents)) {
                        reason = "bad <hx> <hy> <hz>";

                        return null;
                    }
                } else if (args.Length != 1) {
                    reason = "slab takes zero or three dimensions: slab [hx hy hz]";

                    return null;
                }

                return new WorldSceneRow.Slab(Id: m_drag.NextFreeSceneRowId(prefix: "slab-"), Center: focus, HalfExtents: halfExtents, Round: DefaultSlabRound, Smooth: DefaultSlabSmooth, Albedo: s_defaultSlabAlbedo);
            }
            default:
                reason = $"unknown kind '{args[0]}' — boulder|slab";

                return null;
        }
    }

    private CommandResult SpawnHandler(CommandContext context, string[] args, bool slab, string verb) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: verb) is { } guard) {
            return guard;
        }

        var focus = m_session.Focus(slot: slot);
        WorldSceneRow row = (slab
            ? new WorldSceneRow.Slab(Id: m_drag.NextFreeSceneRowId(prefix: "slab-"), Center: focus, HalfExtents: s_defaultSlabHalfExtents, Round: DefaultSlabRound, Smooth: DefaultSlabSmooth, Albedo: s_defaultSlabAlbedo)
            : new WorldSceneRow.Boulder(Id: m_drag.NextFreeSceneRowId(prefix: "boulder-"), Center: focus, Radius: DefaultBoulderRadius, Smooth: DefaultBoulderSmooth));

        if (!m_drag.TrySpawnGhost(slot: slot, row: row, error: out var reason)) {
            return Error(text: $"[{verb}: {reason}]");
        }

        return Echo(slot: slot, verb: verb, detail: $"ghost scene '{row.Id}' — sticks move it, {GrabCommand} commits, {CancelCommand} discards");
    }

    private CommandResult SnapHandler(CommandContext context, string[] args) {
        // Shapes: none = toggle (the chord act); [on|off|<pitch>] [seat] — the first token is ALWAYS the mode when
        // present (so `editor.snap 1` is a 1-unit pitch, never a seat), the seat rides second.
        var hasMode = (args.Length >= 1);
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 1, verb: SnapCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: SnapCommand) is { } guard) {
            return guard;
        }

        var snap = m_drag.Snap(slot: slot);

        if (!hasMode) {
            snap = m_drag.SetSnapEnabled(slot: slot, enabled: !snap.Enabled);
        } else {
            switch (args[0].ToUpperInvariant()) {
                case "ON":
                    snap = m_drag.SetSnapEnabled(slot: slot, enabled: true);

                    break;
                case "OFF":
                    snap = m_drag.SetSnapEnabled(slot: slot, enabled: false);

                    break;
                default:
                    if (!TryFloat(token: args[0], value: out var pitch) || (pitch <= 0f)) {
                        return Error(text: $"[{SnapCommand}: expected on|off|<pitch> — got '{args[0]}']");
                    }

                    snap = m_drag.SetSnapPitch(slot: slot, pitch: pitch);

                    break;
            }
        }

        return Echo(slot: slot, verb: SnapCommand, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(snap.Enabled ? "on" : "off")} pitch={snap.Pitch.X:0.##}"
        ));
    }

    // The shared editing guard: every targeting/manipulation act needs the seat in editor mode (the camera supplies
    // the focus point and pick ray).
    private CommandResult? Guard(int slot, string verb) {
        if (m_session.IsEditing(slot: slot)) {
            return null;
        }

        return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
    }

    private string DescribeSelection(int slot, in EditorSelection selection) {
        var position = (m_targeting.SelectionPosition(slot: slot) ?? default);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{selection.Describe()} at ({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00})"
        );
    }

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };

    private static WorldSection? ParseSection(string token) => token.ToUpperInvariant() switch {
        "SCENE" => WorldSection.Scene,
        "SCREENS" => WorldSection.Screens,
        "SPAWNS" => WorldSection.Spawns,
        "CAMERAS" => WorldSection.Cameras,
        _ => null,
    };

    // The shared FINITE parse boundary (UIE-2) — the drag/move/place/snap twins of EditorCommandModule.TryFloat.
    private static bool TryFloat(string token, out float value) =>
        (float.TryParse(s: token, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out value) && float.IsFinite(f: value));

    private static bool TryVector(string[] args, int at, out Vector3 value) {
        value = default;

        if (!TryFloat(token: args[at], value: out var x) ||
            !TryFloat(token: args[(at + 1)], value: out var y) ||
            !TryFloat(token: args[(at + 2)], value: out var z)) {
            return false;
        }

        value = new Vector3(x: x, y: y, z: z);

        return true;
    }
}
