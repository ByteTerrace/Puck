using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The selection-and-manipulation console surface — the typed twin of every chord act, driven by the
/// game-studio numeric-entry need. The targeting verb (<c>editor.select</c>) both selects a document row by
/// section+id and folds the next/prev proximity cycle and the deselect act onto itself: <c>editor.select
/// [&lt;section&gt; &lt;id&gt; | next | prev | none]</c> — bound with no argument on the select page's D-pad
/// Right/Left (a constant Axis1D value picks next/prev) and West (a plain digital press means deselect, the
/// simplest action, matching a genuinely argless typed line); <c>editor.pick</c> is its crosshair-driven sibling.
/// The drag verbs (<c>editor.grab</c>/<c>drag</c>/<c>release</c>/<c>cancel</c>/<c>spawn.*</c>) drive the pending-row
/// preview channel and commit one whole-row mutation on the release edge; the discrete verbs (<c>editor.move</c>/
/// <c>place</c>/<c>delete</c>) submit an immediate whole-row mutation per act — <c>editor.move</c> is always an
/// absolute placement; a relative nudge is scripted as a move from a read-back <c>editor.status</c>/<c>world.save</c>
/// position (there is no relative twin — a second seat's concurrent move between the read and the write would race
/// the delta against a position that could already be stale by the time it applied). Mutations carry the acting seat
/// principal, so grant denials land on the seat that asked. A separate module from <see cref="EditorCommandModule"/>
/// keeps every class under its analyzer ceilings.
/// </summary>
/// <remarks>Verbs that submit a mutation route Simulation (the stdin barrier then serializes a following
/// <c>world.status</c>/<c>editor.status</c> read-after-write); pure client-state verbs stay Immediate — including
/// <c>editor.drag</c>, whose motion never crosses the wire (that is the whole point of the channel).</remarks>
internal sealed class EditorSelectionCommandModule(WorldEditorSession session, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldClient client, IServerLink link) : ICommandModule {
    /// <summary>The crosshair pick act (South on the select page; North on the camera page — the focus-selection).</summary>
    public const string PickCommand = "editor.pick";
    /// <summary>The select verb: <c>editor.select [&lt;section&gt; &lt;id&gt; | next | prev | none]</c>. D-pad
    /// Right/Left on the select page bind it with a constant Axis1D value (+1 next, -1 prev); West binds it with no
    /// override (a plain digital press means none/deselect).</summary>
    public const string SelectCommand = "editor.select";
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

    private readonly WorldEditorSession m_session = session;
    private readonly WorldEditorTargeting m_targeting = targeting;
    private readonly WorldEditorDrag m_drag = drag;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SelectCommand,
            description: "Selects a document row explicitly (editor.select <screens|spawns|cameras|placements|speakers> <id-or-index> [seat]), OR folds the proximity cycle and deselect acts onto the same verb: editor.select [next|prev|none] [seat]. 'next'/'prev' cycle the nearest-first candidate ring around the editor focus point (wraps; BOUNDED — the nearest CandidateCap rows within CandidateRadius, defaults 16 rows / 32u) — the D-pad Right/Left chord twins, bound with a constant value in place of the argument. 'none' (or no argument at all — the select page's West chord, also bound with a constant) clears the selection. Screens key by engine index; every other section by its stable string id. Selection is client state (never protocol); a selected placement tints in the render, and a selected speaker's gizmo chip lights accent.",
            handler: SelectHandler,
            // Every binding row targeting this verb carries a constant Axis1D value (+1 next, -1 prev, 0 none) — no
            // row sends a plain digital press — declared here so BindingVocabularyCheck admits every row instead of
            // rejecting every future recompose that touches this seat. See CommandDefinition.WithWireArgs's doctrine
            // comment.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: PickCommand,
            description: "Picks the row under the editor camera's crosshair via the document-derived fixed-point picking ray: editor.pick [seat]. Screens pick by their real geometry; placements, spawns, and fixed cameras use proxy spheres. The chord twins are South on the RT select page and North on the LT camera page.",
            handler: PickHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: DeleteCommand,
            description: "Deletes the selected row as one whole-row mutation: editor.delete [seat] — RemoveScreen / RemoveCamera / RemovePlacement / RemoveSpeaker by section; a spawn delete resends the spawn list minus the row (and rejects loudly when the local seats then lack spawns). The chord twin is East on the select page.",
            handler: DeleteHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: GrabCommand,
            description: "Toggles the drag channel on the selection: editor.grab [seat] begins a client-local drag (sticks move the pending row; NOTHING crosses the wire), and a second grab commits it as ONE whole-row mutation. Screens, placements, and fixed/bed speakers drag; move spawns/cameras/anchored speakers with editor.move. The chord twins are South on the place page and North on the select page.",
            handler: GrabHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: ReleaseCommand,
            description: "Commits the live drag as ONE whole-row mutation (the release edge — a whole drag is one journal entry, one undo step): editor.release [seat].",
            handler: ReleaseHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CancelCommand,
            description: "Aborts the live drag — the pending row never existed (no mutation, no journal entry): editor.cancel [seat]. The chord twin is East on the place page.",
            handler: CancelHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.drag",
            description: "Moves the live drag's pending row by a world-space delta — the typed twin of stick drag motion, client-local only: editor.drag <dx> <dy> <dz> [seat]. Snap applies; commit with editor.release.",
            handler: DragHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.move",
            description: "Moves the selected row to an ABSOLUTE position as one whole-row mutation: editor.move <x> <y> <z> [seat]. Screens move their face origin and placements/spawns their position; a fixed camera moves its eye (aim held), an anchored camera/speaker sets its attachment offset; a fixed speaker moves its position, a bed its center. A relative move is scripted as editor.move against a position read back from editor.status/world.save.",
            handler: MoveHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.place",
            description: "Places a creation at the editor focus point as one mutation: editor.place <creationId> [yawDeg [scale]]. See editor.creations / editor.import; placement ids allocate as place-N. Acts for seat 1 when typed. <creationId> is submitted uninterpreted — a dangling reference (naming no creation row) refuses at the tick boundary, against whatever the batch has composed so far, never at this line's own text-submit moment.",
            handler: PlaceHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SnapCommand,
            description: "Grid snap for drags: editor.snap [on|off|<pitch>] [seat]. No argument toggles (the West place-page chord); a pitch sets the X/Z lattice (Y stays free — floor-rest placement) and enables snapping.",
            handler: SnapHandler
        );
    }

    // The select fold: no args -> bound dispatch (direction from a constant Axis1D value, else the West chord's
    // plain-digital deselect); a leading next|prev|none literal -> the same acts, typed; otherwise the original
    // <section> <id> [seat] explicit form. See EditorCommandModule.TryDirection's doctrine comment for the
    // bound-constant mechanism every step-twin fold in this wave shares.
    private CommandResult SelectHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return NoArgSelectHandler(context: context);
        }

        if (args.Is(index: 0, value: "next") || args.Is(index: 0, value: "prev") || args.Is(index: 0, value: "none")) {
            return DirectiveSelectHandler(context: context, args: in args);
        }

        if (args.Count is (< 2 or > 3)) {
            return CommandResult.Error(output: "[editor.select: expected <screens|spawns|cameras|placements|speakers> <id-or-index> [seat], or next|prev|none]");
        }

        var section = ParseSection(token: args[0]);

        if (section is not { } resolvedSection) {
            return CommandResult.Error(output: $"[editor.select: unknown section '{args[0].ToString()}' — screens|spawns|cameras|placements|speakers|next|prev|none]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 2, verb: "editor.select");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: "editor.select") is { } guard) {
            return guard;
        }

        if (!m_targeting.TrySelect(slot: slot, section: resolvedSection, key: args[1].ToString(), selection: out var selection, error: out var reason)) {
            return CommandResult.Error(output: $"[editor.select: {reason}]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.select", detail: DescribeSelection(slot: slot, selection: in selection));
    }
    // The no-arg fold: CommandContext.Source (non-null only for a bound dispatch — see
    // CommandDefinition.WithWireArgs's doctrine comment; Value.Kind is not a safe discriminator once this verb
    // declares Axis1D) distinguishes a bound row from a typed no-arg line. Bound: axis>0 next, axis<0 prev, axis==0
    // deselect (the West chord's own constant). Typed (Source null): deselect too — it always succeeds and clears
    // the selection.
    private CommandResult NoArgSelectHandler(CommandContext context) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: WireArgs.Empty, at: 0, verb: "editor.select");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: "editor.select") is { } guard) {
            return guard;
        }

        if (context.Source is not null) {
            var axis = context.Value.AsAxis1D;

            if (axis > 0f) {
                return CycleCore(slot: slot, direction: 1);
            }

            if (axis < 0f) {
                return CycleCore(slot: slot, direction: -1);
            }
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.select", detail: (m_targeting.Deselect(slot: slot) ? "cleared" : "nothing selected"));
    }
    private CommandResult DirectiveSelectHandler(CommandContext context, in WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 1, verb: "editor.select");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: "editor.select") is { } guard) {
            return guard;
        }

        if (args.Is(index: 0, value: "none")) {
            return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.select", detail: (m_targeting.Deselect(slot: slot) ? "cleared" : "nothing selected"));
        }

        return CycleCore(slot: slot, direction: (args.Is(index: 0, value: "next") ? 1 : -1));
    }
    // Shared next/prev cycle body — the nearest-first proximity ring around the editor focus point.
    private CommandResult CycleCore(int slot, int direction) {
        if (m_targeting.Cycle(slot: slot, direction: direction) is not { } cycled) {
            return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.select", detail: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"no candidates within {m_targeting.CandidateRadius:0}u — fly closer, or editor.select by id"
            ));
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.select", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{cycled.Selection.Describe()} {cycled.Distance:0.0}u of {cycled.Count} candidates (r {m_targeting.CandidateRadius:0}u, cap {m_targeting.CandidateCap})"
        ));
    }
    private CommandResult PickHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: PickCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: PickCommand) is { } guard) {
            return guard;
        }

        if (!m_targeting.TryPick(slot: slot, selection: out var selection)) {
            return EditorSculptCommandModule.Echo(slot: slot, verb: PickCommand, detail: "nothing under the crosshair");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: PickCommand, detail: $"selected {DescribeSelection(slot: slot, selection: in selection)}");
    }
    private CommandResult DeleteHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: DeleteCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: DeleteCommand) is { } guard) {
            return guard;
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return CommandResult.Error(output: $"[{DeleteCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection]");
        }

        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        var principal = context.ActingPrincipal();

        switch (selection.Section) {
            case WorldSection.Screens:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveScreen(Principal: principal, Index: selection.Index));

                break;
            case WorldSection.Cameras:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveCamera(Principal: principal, Name: selection.Id));

                break;
            case WorldSection.Placements:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemovePlacement(Principal: principal, Id: selection.Id));

                break;
            case WorldSection.Speakers:
                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveSpeaker(Principal: principal, Name: selection.Id));

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
                return CommandResult.Error(output: $"[{DeleteCommand}: {selection.Describe()} has no remove mutation]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: DeleteCommand, detail: $"{selection.Describe()} — remove submitted");
    }
    private CommandResult GrabHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: GrabCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: GrabCommand) is { } guard) {
            return guard;
        }

        // The toggle: a live drag commits (the pad's one-button grab→move→commit flow); otherwise a grab begins.
        if (m_drag.IsDragging(slot: slot)) {
            return ReleaseCore(principal: context.ActingPrincipal(), slot: slot, verb: GrabCommand);
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return CommandResult.Error(output: $"[{GrabCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection — editor.pick or editor.select next first]");
        }

        if (!m_drag.TryGrab(slot: slot, selection: in selection, error: out var reason)) {
            return CommandResult.Error(output: $"[{GrabCommand}: {reason}]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: GrabCommand, detail: $"dragging {selection.Describe()} — sticks move it, grab again commits, {CancelCommand} aborts");
    }
    private CommandResult ReleaseHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: ReleaseCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: ReleaseCommand) is { } guard) {
            return guard;
        }

        return ReleaseCore(principal: context.ActingPrincipal(), slot: slot, verb: ReleaseCommand);
    }
    private CommandResult ReleaseCore(WorldPrincipal principal, int slot, string verb) {
        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        if (m_drag.Release(slot: slot, principal: principal) is not { } echo) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: verb, detail: echo);
    }
    private CommandResult CancelHandler(CommandContext context, WireArgs args) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: CancelCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: CancelCommand) is { } guard) {
            return guard;
        }

        if (m_drag.Cancel(slot: slot) is not { } echo) {
            return CommandResult.Error(output: $"[{CancelCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: CancelCommand, detail: echo);
    }
    private CommandResult DragHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 3 or > 4)) {
            return CommandResult.Error(output: "[editor.drag: expected <dx> <dy> <dz> [seat]]");
        }

        if (!TryVector(args: args, at: 0, value: out var delta)) {
            return CommandResult.Error(output: "[editor.drag: could not parse <dx> <dy> <dz> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 3, verb: "editor.drag");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: "editor.drag") is { } guard) {
            return guard;
        }

        if (!m_drag.IsDragging(slot: slot)) {
            return CommandResult.Error(output: $"[editor.drag: seat {PlayerRoster.DisplayNumber(slot: slot)} has no live drag — editor.grab first]");
        }

        m_drag.Move(slot: slot, delta: delta);

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.drag", detail: (m_drag.Describe(slot: slot) ?? "moved"));
    }
    private CommandResult MoveHandler(CommandContext context, WireArgs args) {
        const string verb = "editor.move";

        if (args.Count is (< 3 or > 4)) {
            return CommandResult.Error(output: $"[{verb}: expected <x> <y> <z> [seat]]");
        }

        if (!TryVector(args: args, at: 0, value: out var value)) {
            return CommandResult.Error(output: $"[{verb}: could not parse <x> <y> <z> as finite numbers]");
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 3, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: verb) is { } guard) {
            return guard;
        }

        if (m_targeting.Selected(slot: slot) is not { } selection) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no selection]");
        }

        if (!TrySubmitMove(principal: context.ActingPrincipal(), selection: in selection, value: value, target: out var target, reason: out var reason)) {
            return CommandResult.Error(output: $"[{verb}: {reason}]");
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: verb, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{selection.Describe()} -> ({target.X:0.00}, {target.Y:0.00}, {target.Z:0.00}) — one mutation submitted"
        ));
    }

    // Compose the selected row's whole-row ABSOLUTE move mutation. The target semantics per section are documented
    // on editor.move. Absolute-only — a relative move is scripted
    // as editor.move against a position read back from editor.status/world.save.
    private bool TrySubmitMove(WorldPrincipal principal, in EditorSelection selection, Vector3 value, out Vector3 target, out string reason) {
        target = value;
        reason = string.Empty;

        var definition = m_client.Definition;

        switch (selection.Section) {
            case WorldSection.Screens:
                foreach (var screen in definition.Screens) {
                    if (screen.Index == selection.Index) {
                        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: (screen with { Origin = target })));

                        return true;
                    }
                }

                break;
            case WorldSection.Placements:
                foreach (var placement in definition.Placements) {
                    if (string.Equals(a: placement.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertPlacement(Principal: principal, Placement: (placement with { Position = target })));

                        return true;
                    }
                }

                break;
            case WorldSection.Spawns: {
                    var spawns = new List<WorldSpawnPoint>(capacity: definition.SpawnPoints.Count);
                    var found = false;

                    foreach (var spawn in definition.SpawnPoints) {
                        if (string.Equals(a: spawn.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
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

                    var movedMotion = WorldCameraRigCompiler.Move(motion: camera.Rig.Motion, value: value, relative: false);

                    target = WorldCameraRigCompiler.AuthoredPosition(motion: movedMotion);

                    // An absolute move re-poses the eye and holds the authored aim point (no parallel aim shift —
                    // that was the relative twin's behavior).
                    var moved = (camera with { Rig = camera.Rig with { Motion = movedMotion } });

                    m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCamera(Principal: principal, Camera: moved));

                    return true;
                }

                break;
            case WorldSection.Speakers:
                // The camera pattern's audio sibling: Fixed moves its position, a Bed its center, an Anchored its
                // attachment OFFSET (the documented v1 numeric channel for anchored rows).
                foreach (var speaker in definition.Speakers) {
                    if (!string.Equals(a: speaker.Name, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }

                    switch (speaker) {
                        case WorldSpeaker.Fixed fixedSpeaker:
                            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: principal, Speaker: (fixedSpeaker with { Position = target })));

                            return true;
                        case WorldSpeaker.Bed bed:
                            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: principal, Speaker: (bed with { Center = target })));

                            return true;
                        case WorldSpeaker.Anchored anchored:
                            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: principal, Speaker: (anchored with { Offset = target })));

                            return true;
                    }
                }

                break;
        }

        reason = $"no {selection.Describe()} in the live definition";

        return false;
    }
    private CommandResult PlaceHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return CommandResult.Error(output: "[editor.place: expected <creationId> [yawDeg [scale]]]");
        }

        var slot = ((context.Parse is null) ? context.Slot : 0);

        if (m_session.NotEditingError(slot: slot, verb: "editor.place") is { } guard) {
            return guard;
        }

        return PlaceCreation(principal: context.ActingPrincipal(), slot: slot, args: args, focus: m_session.Focus(slot: slot));
    }

    // The creation stamp: editor.place <creationId> [yawDeg [scale]] — an immediate whole-row UpsertPlacement at the
    // editor focus point (the ghost/drag flow is editor.spawn.creation on the place page). <creationId> is NOT
    // resolved against the live definition here — the door-not-type shape this repository ruled against: a
    // verb-level FindCreation would race a same-batch editor.sculpt.commit/world.row.set creations declaring the very
    // row this line names, refusing nondeterministically depending on whether that prior mutation has composed yet.
    // WorldDefinitionValidator already refuses a dangling placement.creationId by name at the tick boundary — the
    // ONE door, asked against the candidate this batch has actually built.
    private CommandResult PlaceCreation(WorldPrincipal principal, int slot, in WireArgs args, Vector3 focus) {
        var yaw = 0f;
        var scale = 1f;

        if ((args.Count >= 2) && !EditorCommandModule.TryFloat(token: args[1], value: out yaw)) {
            return CommandResult.Error(output: $"[editor.place: bad yawDeg '{args[1].ToString()}']");
        }

        if ((args.Count >= 3) && !EditorCommandModule.TryFloat(token: args[2], value: out scale)) {
            return CommandResult.Error(output: $"[editor.place: bad scale '{args[2].ToString()}']");
        }

        var placement = new WorldPlacement(
            Id: m_drag.NextFreePlacementId(),
            CreationId: args[0].ToString(),
            Position: focus,
            YawDegrees: yaw,
            Scale: scale
        );

        // The acting principal is the one this dispatch's ingress door stamped (see WorldPrincipalMapping) — a chord
        // act carries the pressing seat's own claim, a typed line carries Console; the handler never re-derives it.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertPlacement(Principal: principal, Placement: placement));

        return EditorSculptCommandModule.Echo(slot: slot, verb: "editor.place", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"placement '{placement.Id}' of '{placement.CreationId}' at ({focus.X:0.00}, {focus.Y:0.00}, {focus.Z:0.00}) — one mutation submitted"
        ));
    }
    private CommandResult SnapHandler(CommandContext context, WireArgs args) {
        // Shapes: none = toggle (the chord act); [on|off|<pitch>] [seat] — the first token is ALWAYS the mode when
        // present (so `editor.snap 1` is a 1-unit pitch, never a seat), the seat rides second.
        var hasMode = (args.Count >= 1);

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 1, verb: SnapCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(slot: slot, verb: SnapCommand) is { } guard) {
            return guard;
        }

        var snap = m_drag.Snap(slot: slot);

        if (!hasMode) {
            snap = m_drag.SetSnapEnabled(slot: slot, enabled: !snap.Enabled);
        } else if (args.Is(index: 0, value: "on")) {
            snap = m_drag.SetSnapEnabled(slot: slot, enabled: true);
        } else if (args.Is(index: 0, value: "off")) {
            snap = m_drag.SetSnapEnabled(slot: slot, enabled: false);
        } else {
            if (!EditorCommandModule.TryFloat(token: args[0], value: out var pitch) || (pitch <= 0f)) {
                return CommandResult.Error(output: $"[{SnapCommand}: expected on|off|<pitch> — got '{args[0].ToString()}']");
            }

            snap = m_drag.SetSnapPitch(slot: slot, pitch: pitch);
        }

        return EditorSculptCommandModule.Echo(slot: slot, verb: SnapCommand, detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(snap.Enabled ? "on" : "off")} pitch={snap.Pitch.X:0.##}"
        ));
    }
    private string DescribeSelection(int slot, in EditorSelection selection) {
        var position = (m_targeting.SelectionPosition(slot: slot) ?? default);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{selection.Describe()} at ({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00})"
        );
    }
    private static WorldSection? ParseSection(ReadOnlySpan<char> token) {
        if (token.Equals(other: "screens", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return WorldSection.Screens;
        }

        if (token.Equals(other: "spawns", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return WorldSection.Spawns;
        }

        if (token.Equals(other: "cameras", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return WorldSection.Cameras;
        }

        if (token.Equals(other: "placements", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return WorldSection.Placements;
        }

        if (token.Equals(other: "speakers", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return WorldSection.Speakers;
        }

        return null;
    }
    private static bool TryVector(in WireArgs args, int at, out Vector3 value) {
        value = default;

        if (!EditorCommandModule.TryFloat(token: args[at], value: out var x) ||
            !EditorCommandModule.TryFloat(token: args[(at + 1)], value: out var y) ||
            !EditorCommandModule.TryFloat(token: args[(at + 2)], value: out var z)) {
            return false;
        }

        value = new Vector3(x: x, y: y, z: z);

        return true;
    }
}
