using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

// A seat's edits to one placement run one at a time per world. An edit is based on the latest value the seat submitted
// or queued for that placement, never on the document as this host last saw it, which trails every edit still in
// flight (by a tick in the console's world, by a round trip in a world across a federation seam). While an edit is in
// flight, the next is queued, and each further one supersedes the queued one, since it was composed on top of it. The
// world's verdict settles the one in flight: applied, it becomes the confirmed value and the queued edit is submitted;
// refused, the queued edit is dropped with it, the placement rolls back to the confirmed value, and the rollback is named.
// So edits made faster than verdicts arrive all land, and a refused edit never reappears inside a later one.
public sealed partial class WorldEditorCommandModule {
    private readonly Lock m_pendingGate = new();
    private readonly Dictionary<(string World, string Id), PendingEdit> m_pending = [];

    // One placement's edits in one world: the row before the first of them, the last value the world confirmed and the
    // document it had delivered when it did, the edit in flight, and the edit queued behind it.
    private sealed class PendingEdit {
        public WorldPlacement? Confirmed { get; set; }
        public WorldDefinition? ConfirmedUnder { get; set; }
        public WorldPlacement? InFlight { get; set; }
        public WorldPlacement? Origin { get; init; }
        public WorldPlacement? Queued { get; set; }
    }

    // The row an edit to a placement is based on: the latest edit queued or in flight; else the value its world last
    // confirmed, until the world delivers a document after that confirmation; else the delivered row.
    private WorldPlacement? LatestOf(EditWorld world, string id) {
        var delivered = WorldDefinitionRows.FindPlacement(id: id, placements: world.Definition.Placements);

        lock (m_pendingGate) {
            if (!m_pending.TryGetValue(key: (world.Name, id), value: out var edit)) {
                return delivered;
            }

            if ((edit.Queued ?? edit.InFlight) is { } latest) {
                return latest;
            }

            if (ReferenceEquals(objA: edit.ConfirmedUnder, objB: world.Definition)) {
                return (edit.Confirmed ?? delivered);
            }

            _ = m_pending.Remove(key: (world.Name, id));

            return delivered;
        }
    }
    // Submits an edit, or queues it behind the one in flight, and makes the placement the seat's current one there.
    private CommandResult SubmitPlacement(CommandContext context, EditWorld world, WorldPlacement placement, WorldPlacement? from, string verb, int slot) {
        var key = (world.Name, placement.Id);
        var identity = WorldRowCommandModule.RowIdentityOf(key: placement.Id, path: PlacementsPath);

        lock (m_pendingGate) {
            if (m_pending.TryGetValue(key: key, value: out var edit) && (edit.InFlight is not null)) {
                edit.Queued = placement;
                seats.SetCurrent(placement: placement.Id, slot: slot, world: world.Name);

                return Echo(context: context, pending: "queued", placement: placement, slot: slot, verb: verb, world: world);
            }
        }

        if (world.Guard?.IsClaimed(rowIdentity: identity, window: world.Window) is true) {
            return CommandResult.Error(output: $"[{verb}: '{placement.Id}' already has an edit buffered this tick in '{world.Name}'; fence with world.wait]");
        }

        if (!TryCompose(context: context, mutation: out var mutation, placement: placement, refusal: out var refusal, verb: verb)) {
            return refusal;
        }

        lock (m_pendingGate) {
            if (!m_pending.TryGetValue(key: key, value: out var edit)) {
                edit = new PendingEdit { Origin = from };
                m_pending[key] = edit;
            }

            edit.InFlight = placement;
        }

        var submitted = world.Link.Submit(
            echoes: echoes,
            mutation: mutation,
            observe: result => Settle(context: context, id: placement.Id, result: result, verb: verb, world: world),
            verb: verb
        );

        if (submitted.IsError) {
            lock (m_pendingGate) {
                _ = m_pending.Remove(key: key);
            }

            return submitted;
        }

        world.Guard?.Claim(rowIdentity: identity);
        seats.SetCurrent(placement: placement.Id, slot: slot, world: world.Name);

        return Echo(context: context, pending: "submitted", placement: placement, slot: slot, verb: verb, world: world);
    }
    // Settles the edit in flight with its world's verdict, on whatever thread the link completes on: applied, it is
    // confirmed and the queued edit goes next; refused, the queued edit is dropped and the placement rolls back, named.
    private void Settle(CommandContext context, EditWorld world, string id, WorldSubmissionResult result, string verb) {
        WorldPlacement? next = null;
        WorldPlacement? rolledBackTo = null;
        var refused = false;

        lock (m_pendingGate) {
            if (!m_pending.TryGetValue(key: (world.Name, id), value: out var edit) || (edit.InFlight is not { } settled)) {
                return;
            }

            if (result is WorldSubmissionResult.Mutation { Outcome.Applied: true }) {
                edit.Confirmed = settled;
                edit.ConfirmedUnder = world.Delivered();
                edit.InFlight = next = edit.Queued;
                edit.Queued = null;
            } else {
                refused = true;
                rolledBackTo = (edit.Confirmed ?? edit.Origin);
                edit.InFlight = null;
                edit.Queued = null;
                edit.ConfirmedUnder = world.Delivered();
                edit.Confirmed = rolledBackTo;
            }
        }

        if (refused) {
            echoes.Publish(result: CommandResult.Error(output: $"[{verb}: '{id}' in '{world.Name}' rolled back to {((rolledBackTo is { } row) ? Format(value: ((Vector3)row.Position)) : "nothing: it was never placed")}]"));

            return;
        }

        if (next is not { } queued) {
            return;
        }

        if (!TryCompose(context: context, mutation: out var mutation, placement: queued, refusal: out var composeRefusal, verb: verb)) {
            Settle(context: context, id: id, result: new WorldSubmissionResult.Refusal(Code: "world.editor.compose", Detail: composeRefusal.Output), verb: verb, world: world);

            return;
        }

        var submitted = world.Link.Submit(
            echoes: echoes,
            mutation: mutation,
            observe: late => Settle(context: context, id: id, result: late, verb: verb, world: world),
            verb: verb
        );

        if (!string.IsNullOrEmpty(value: submitted.Output)) {
            echoes.Publish(result: submitted);
        }
    }
    private bool TryCompose(CommandContext context, WorldPlacement placement, string verb, out WorldMutation mutation, out CommandResult refusal) {
        if (!WorldRowCommandModule.TryComposeRoutedSet(
            error: out var reason,
            json: JsonSerializer.Serialize(value: placement, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement),
            mutation: out var composed,
            path: PlacementsPath,
            principal: context.Principal
        )) {
            mutation = null!;
            refusal = CommandResult.Error(output: $"[{verb}: '{placement.Id}': {reason}]");

            return false;
        }

        mutation = composed!;
        refusal = CommandResult.None;

        return true;
    }
    private static CommandResult Echo(CommandContext context, EditWorld world, WorldPlacement placement, string pending, string verb, int slot) => new(Output: CommandEcho.Open(verb: verb)
        .Field(key: "seat", value: PlayerRoster.DisplayNumber(slot: slot))
        .Field(key: "world", value: world.Name)
        .Field(key: "placement", value: placement.Id)
        .Field(key: "prototype", value: placement.PrototypeId)
        .Field(key: "position", value: Format(value: ((Vector3)placement.Position)))
        .Field(key: "yaw", value: Format(value: placement.YawDegrees))
        .Field(key: "edit", value: pending)
        .Field(key: "as", value: context.Principal.Describe())
        .Close());
}
