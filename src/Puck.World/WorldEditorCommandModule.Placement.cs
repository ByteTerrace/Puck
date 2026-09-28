using System.Numerics;
using Puck.Commands;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldEditorCommandModule {
    /// <summary>How far ahead of a seat's camera a placement lands when its aim meets no surface, in world units.</summary>
    public const float AheadDistance = 4f;
    /// <summary>The verb that moves a placement by whole grid steps.</summary>
    public const string NudgeCommand = "world.nudge";
    /// <summary>The verb that puts a placement down where a seat aims.</summary>
    public const string PlaceCommand = "world.place";
    /// <summary>The verb that turns a placement by whole angle steps.</summary>
    public const string TurnCommand = "world.turn";

    // A surface steeper than this (the normal's vertical component below it) is a wall, which a placement does not rest on.
    private const float RestingNormalY = 0.5f;
    private const string PlacementsPath = "placements";

    private static int? AxisOf(ReadOnlySpan<char> token) => token switch {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => null,
    };
    private static float Component(Vector3 value, int axis) => axis switch {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };
    // Where a placement put down along a seat's aim lands: the first solid surface the aim meets, else a fixed distance
    // ahead; snapped through GridSnap. When snapping rests placements on surfaces and the aim met an upward-facing one,
    // the vertical axis is left to the surface: the snapped column is dropped back onto the surface beneath it.
    private Vector3 Land(int slot, WorldDefinition definition, WorldEditorRay aim, Vector3 pitch, WorldEditorSnap snap, SnapReference? reference, Vector3 halfExtents) {
        var hit = seats.SurfaceProbe?.Invoke(
            arg1: slot,
            arg2: aim,
            arg3: WorldRenderFarDistance.Resolve(defaults: definition.Render)
        );
        var point = (hit?.Point ?? (aim.Origin + (Vector3.Normalize(value: aim.Direction) * AheadDistance)));
        var rest = (snap.Surface && (hit is { Normal.Y: > RestingNormalY }));
        var config = new SnapConfig(
            AngleStepDegrees: snap.AngleStepDegrees,
            Enabled: snap.Enabled,
            Pitch: (rest ? (pitch with { Y = 0f }) : pitch),
            Reference: ((rest && (reference is { } captured)) ? (captured with { Pitch = (captured.Pitch with { Y = 0f }) }) : reference)
        );
        var snapped = GridSnap.Apply(
            candidateLocalHalfExtents: halfExtents,
            config: in config,
            intent: point,
            previousSnapped: point
        );

        if (!rest || ((snapped.X == point.X) && (snapped.Z == point.Z))) {
            return snapped;
        }

        var lift = (2f * MathF.Max(x: pitch.X, y: MathF.Max(x: pitch.Y, y: pitch.Z)));
        var below = seats.SurfaceProbe?.Invoke(
            arg1: slot,
            arg2: new WorldEditorRay(Direction: -Vector3.UnitY, Origin: new Vector3(x: snapped.X, y: (point.Y + lift), z: snapped.Z)),
            arg3: (2f * lift)
        );

        return (snapped with { Y = ((below is { Normal.Y: > RestingNormalY } ground) ? ground.Point.Y : point.Y) });
    }
    private SnapReference? ReferenceFor(WorldDefinition definition, string world, int slot, Vector3 pitch, string? excluding) => (
        ((seats.ReferenceOf(slot: slot, world: world) is { } id) &&
        !string.Equals(a: id, b: excluding, comparisonType: StringComparison.Ordinal) &&
        (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement))
            ? WorldEditorGeometry.ReferenceOf(definition: definition, pitch: pitch, placement: placement)
            : null
    );
    // The placement a nudge or turn acts on: the named one, else the seat's current one there.
    private bool TryTargetId(EditWorld world, int slot, string? named, string verb, out string id, out CommandResult refusal) {
        refusal = CommandResult.None;

        if ((named ?? seats.CurrentOf(slot: slot, world: world.Name)) is { } found) {
            id = found;

            return true;
        }

        id = string.Empty;
        refusal = CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no current placement; name one]");

        return false;
    }
    // Whether a nudge or turn may act on its base (the placement as its line holds it, against the document its source
    // delivers now): refused when there is none, and when its position is resolved through something else (a parent, an
    // attachment, a board), which is what to move instead.
    private static bool TryMovable(EditWorld world, WorldDefinition definition, string id, WorldPlacement? basis, string verb, out CommandResult refusal) {
        refusal = CommandResult.None;

        if (basis is null) {
            refusal = CommandResult.Error(output: $"[{verb}: no placement '{id}' in '{world.Name}']");

            return false;
        }

        if (
            (basis.Position.Reference is not null) ||
            ((WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } delivered) &&
            (WorldDefinitionRows.ResolvedPosition(definition: definition, placement: delivered) != ((Vector3)delivered.Position)))
        ) {
            refusal = CommandResult.Error(output: $"[{verb}: '{id}' is positioned through another row; move that instead]");

            return false;
        }

        return true;
    }
    // Edits one placement: its base is read, the edit composed on it against the document its source delivers now, and
    // the edit admitted and sent, as one step under its world's queue (WorldEditorEditQueue.Offer).
    private CommandResult EditPlacement(CommandContext context, EditWorld world, string id, string verb, Func<WorldDefinition, WorldPlacement, WorldPlacement> change) {
        if (!TryTargetOf(refusal: out var refusal, source: out var source, target: out var target, verb: verb, world: world)) {
            return refusal;
        }

        var admission = target.Queue.Offer(
            compose: (WorldDeliveredDocument document, WorldPlacement? basis, bool queues, out CommandResult composeRefusal) => (TryMovable(basis: basis, definition: document.Definition, id: id, refusal: out composeRefusal, verb: verb, world: world)
                ? ComposeEdit(context: context, queues: queues, refusal: out composeRefusal, row: change(arg1: document.Definition, arg2: basis!), source: source, verb: verb, world: world)
                : null),
            id: id,
            source: source,
            verb: verb
        );

        return Admit(admission: admission, context: context, slot: context.Slot, world: world);
    }
    private CommandResult NudgeHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return CommandResult.Usage(form: "[<placement>] x|y|z <steps>", verb: NudgeCommand);
        }

        var at = (args.Count - 2);

        if ((AxisOf(token: args[at]) is not { } axis) || !args.TryInt(index: (at + 1), value: out var steps) || (steps == 0)) {
            return CommandResult.Usage(form: "[<placement>] x|y|z <steps> (steps a non-zero integer)", verb: NudgeCommand);
        }

        if (!TryEditWorld(context: context, refusal: out var editRefusal, verb: NudgeCommand, world: out var world)) {
            return editRefusal;
        }

        var slot = context.Slot;

        if (!TryTargetId(id: out var id, named: ((at == 1) ? args[0].ToString() : null), refusal: out var refusal, slot: slot, verb: NudgeCommand, world: world)) {
            return refusal;
        }

        var unit = axis switch {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };

        return EditPlacement(
            change: (definition, placement) => {
                var pitch = seats.GridOf(document: definition.Editor, slot: slot).ResolvedPitch;
                var snap = seats.SnapOf(document: definition.Editor, slot: slot);
                var position = ((Vector3)placement.Position);
                Vector3 moved;

                if (ReferenceFor(definition: definition, world: world.Name, excluding: id, pitch: pitch, slot: slot) is { } reference) {
                    var local = Vector3.Transform(rotation: Quaternion.Inverse(value: reference.Frame), value: (position - reference.Origin));

                    local += (unit * (steps * Component(axis: axis, value: reference.Pitch)));

                    if (snap.Enabled) {
                        local = GridSnap.SnapToWorldLattice(p: local, pitch: (reference.Pitch * unit));
                    }

                    moved = (reference.Origin + Vector3.Transform(rotation: reference.Frame, value: local));
                } else {
                    moved = (position + (unit * (steps * Component(axis: axis, value: pitch))));

                    if (snap.Enabled) {
                        moved = GridSnap.SnapToWorldLattice(p: moved, pitch: (pitch * unit));
                    }
                }

                return (placement with { Position = moved });
            },
            context: context,
            id: id,
            verb: NudgeCommand,
            world: world
        );
    }
    private CommandResult PlaceHandler(CommandContext context, WireArgs args) {
        if (args.Count > 2) {
            return CommandResult.Usage(form: "[<prototype>] [<id>]", verb: PlaceCommand);
        }

        if (!TryEditWorld(context: context, refusal: out var editRefusal, verb: PlaceCommand, world: out var world)) {
            return editRefusal;
        }

        if (!TryTargetOf(refusal: out var targetRefusal, source: out var source, target: out var target, verb: PlaceCommand, world: world)) {
            return targetRefusal;
        }

        var slot = context.Slot;
        var currentId = seats.CurrentOf(slot: slot, world: world.Name);
        var named = ((args.Count >= 1) ? args[0].ToString() : null);
        var namedId = ((args.Count == 2) ? args[1].ToString() : null);

        // The id, asked for or minted as the creation's name and the first number neither the document the source delivers
        // now nor any edit holds, is chosen with the edit's admission, so two places before any verdict never share one.
        var admission = target.Queue.OfferNew(
            compose: (WorldDeliveredDocument document, Func<string, WorldPlacement?> latest, Func<string, bool> taken, Func<string, string> mint, out CommandResult refusal) => {
                var definition = document.Definition;
                var current = ((currentId is not null) ? latest(arg: currentId) : null);

                if ((named ?? current?.PrototypeId) is not { } prototype) {
                    refusal = CommandResult.Error(output: $"[{PlaceCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no current placement to copy; name a creation]");

                    return null;
                }

                if (!definition.Creations.Any(predicate: creation => string.Equals(a: creation.Id.Value, b: prototype, comparisonType: StringComparison.Ordinal))) {
                    refusal = CommandResult.Error(output: $"[{PlaceCommand}: no creation '{prototype}']");

                    return null;
                }

                if ((namedId is not null) && taken(arg: namedId)) {
                    refusal = CommandResult.Error(output: $"[{PlaceCommand}: a placement '{namedId}' already exists]");

                    return null;
                }

                if (seats.AimProbe?.Invoke(arg: slot) is not { } aim) {
                    refusal = CommandResult.Error(output: $"[{PlaceCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} presents no view to aim from]");

                    return null;
                }

                // A new placement copies the scale, yaw and solidity of the seat's current placement when that is the
                // same creation, else of the first placement of the creation, so a builder stamps out another like it.
                var template = (((current is not null) && string.Equals(a: current.PrototypeId, b: prototype, comparisonType: StringComparison.Ordinal))
                    ? current
                    : definition.Placements.FirstOrDefault(predicate: placement => string.Equals(a: placement.PrototypeId, b: prototype, comparisonType: StringComparison.Ordinal)));
                var scale = (template?.Scale ?? 1f);
                var pitch = seats.GridOf(document: definition.Editor, slot: slot).ResolvedPitch;
                var snap = seats.SnapOf(document: definition.Editor, slot: slot);
                var yaw = (template?.YawDegrees ?? 0f);

                return ComposeEdit(
                    context: context,
                    queues: false,
                    refusal: out refusal,
                    row: new WorldPlacement(
                        Id: (namedId ?? mint(arg: prototype)),
                        Position: Land(
                            aim: aim,
                            definition: definition,
                            halfExtents: new Vector3(value: (0.5f * scale)),
                            pitch: pitch,
                            reference: ReferenceFor(definition: definition, world: world.Name, excluding: null, pitch: pitch, slot: slot),
                            slot: slot,
                            snap: snap
                        ),
                        PrototypeId: prototype,
                        Scale: scale,
                        Solid: (template?.Solid ?? new WorldSolid(Margin: 0f)),
                        YawDegrees: (snap.Enabled ? GridSnap.SnapYawDegrees(stepDegrees: snap.AngleStepDegrees, yawDegrees: yaw) : yaw)
                    ),
                    source: source,
                    verb: PlaceCommand,
                    world: world
                );
            },
            source: source,
            verb: PlaceCommand
        );

        return Admit(admission: admission, context: context, slot: slot, world: world);
    }
    private IEnumerable<CommandDefinition> PlacementVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: PlaceCommand,
            description: $"Puts a placement down where the acting seat aims: world.place [<creation>] [<id>]. It lands on the first solid surface the seat's pointer ray meets in the presentation's static field, or, with no pointer over the view, the ray through the middle of the view; else {AheadDistance} units ahead of the camera. With snapping on, the position snaps to the grid (or the captured reference's lattice) through GridSnap, and with surface snapping on it rests on the surface under the snapped column. With no creation it stamps another of the seat's current placement's creation; with no id it names the placement after its creation and the first free number. It copies the scale, yaw and solidity of the seat's current placement of that creation, else the first placement of it, else unit scale, no yaw and a solid of margin 0. Submits one placements upsert through world.row.set's section upsert under the issuing principal; the verdict arrives deferred, and world.undo takes it back. The placement becomes the seat's current one.",
            handler: PlaceHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: NudgeCommand,
            description: "Moves a placement by whole grid steps: world.nudge [<placement>] x|y|z <steps> (the seat's current placement when none is named). A step is the seat's grid pitch on that axis, along the world axes, or along the captured reference's own axes and lattice when the seat has one; with snapping on, the moved axis lands on the lattice. A placement positioned through another row (a parent, an attachment, a board) is refused by name. Submits one placements upsert through world.row.set's section upsert under the issuing principal; world.undo takes it back. The placement becomes the seat's current one.",
            handler: NudgeHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: TurnCommand,
            description: "Turns a placement about the vertical axis by whole angle steps: world.turn [<placement>] <steps> (the seat's current placement when none is named). A step is the seat's snapping angle; with snapping on, the yaw lands on a whole multiple of it. Submits one placements upsert through world.row.set's section upsert under the issuing principal; world.undo takes it back. The placement becomes the seat's current one.",
            handler: TurnHandler,
            routing: CommandRouting.Simulation
        );
    }
    private CommandResult TurnHandler(CommandContext context, WireArgs args) {
        if ((args.Count is not (1 or 2)) || !args.TryInt(index: (args.Count - 1), value: out var steps) || (steps == 0)) {
            return CommandResult.Usage(form: "[<placement>] <steps> (steps a non-zero integer)", verb: TurnCommand);
        }

        if (!TryEditWorld(context: context, refusal: out var editRefusal, verb: TurnCommand, world: out var world)) {
            return editRefusal;
        }

        var slot = context.Slot;

        if (!TryTargetId(id: out var id, named: ((args.Count == 2) ? args[0].ToString() : null), refusal: out var refusal, slot: slot, verb: TurnCommand, world: world)) {
            return refusal;
        }

        return EditPlacement(
            change: (definition, placement) => {
                var snap = seats.SnapOf(document: definition.Editor, slot: slot);
                var yaw = (placement.YawDegrees + (steps * snap.AngleStepDegrees));

                if (snap.Enabled) {
                    yaw = GridSnap.SnapYawDegrees(stepDegrees: snap.AngleStepDegrees, yawDegrees: yaw);
                }

                return (placement with { YawDegrees = (yaw % 360f) });
            },
            context: context,
            id: id,
            verb: TurnCommand,
            world: world
        );
    }
}
