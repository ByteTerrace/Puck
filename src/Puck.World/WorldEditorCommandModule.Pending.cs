using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

// Edits to placements run through one WorldEditorEditQueue per world activation, which owns their order, their base
// and their settlement. A queue lives exactly as long as its world does here: it is retired, and every edit it holds
// abandoned by name, when the world's link goes away (its instance stops or is reaped, its endpoint is disposed) or
// when the link starts delivering another activation's documents (a crossing onward, a recreated world).
public sealed partial class WorldEditorCommandModule {
    private readonly Lock m_targetGate = new();
    private readonly Dictionary<(string Authority, Guid Activation), EditTarget> m_targets = [];

    // One world activation's queue, the link its edits go through, the read of the document it last delivered here,
    // and the registrations that retire it with its world.
    private sealed class EditTarget(string name, WorldEditorEditQueue queue, IServerLink link, Func<WorldDeliveredDocument> delivered) {
        public Func<WorldDeliveredDocument> Delivered => delivered;
        public Action<WorldDocumentVersion>? Handler { get; set; }
        public CancellationTokenRegistration Lifetime { get; set; }
        public IServerLink Link => link;
        public WorldSessionMirror? Mirror { get; set; }
        public string Name => name;
        public WorldEditorEditQueue Queue => queue;
    }

    // The queue of the world activation a verb edits, opened on first use and tied to that world's lifetime. A world no
    // authority has delivered a document of yet is refused: nothing it shows could be edited.
    private bool TryTargetOf(EditWorld world, string verb, out EditTarget target, out CommandResult refusal) {
        target = null!;
        refusal = CommandResult.None;

        if (!world.Version.IsDelivered) {
            refusal = CommandResult.Error(output: $"[{verb}: '{world.Name}' has delivered no document yet]");

            return false;
        }

        var key = (world.Authority, world.Version.Activation);
        bool opened;

        lock (m_targetGate) {
            opened = !m_targets.TryGetValue(key: key, value: out var existing);
            target = (existing ?? new EditTarget(
                delivered: world.Delivered,
                link: world.Link,
                name: world.Name,
                queue: new WorldEditorEditQueue(activation: world.Version.Activation)
            ));

            if (opened) {
                m_targets[key] = target;
            }
        }

        if (opened) {
            var opening = target;

            if (world.Mirror is { } mirror) {
                opening.Mirror = mirror;
                opening.Handler = version => {
                    if (!opening.Queue.Deliver(version: version)) {
                        Retire(key: key, reason: "its link now delivers another world", target: opening);
                    }
                };
                mirror.DocumentDelivered += opening.Handler;
            }

            opening.Lifetime = world.Lifetime.Register(callback: () => Retire(key: key, reason: "its world stopped", target: opening));
        }

        return true;
    }
    // Ends a world activation's queue: it leaves the registry, stops listening, and every edit it held is abandoned by
    // name. Total and idempotent, since it runs inside a disposal or a delivery.
    private void Retire((string Authority, Guid Activation) key, EditTarget target, string reason) {
        lock (m_targetGate) {
            if (!m_targets.TryGetValue(key: key, value: out var current) || !ReferenceEquals(objA: current, objB: target)) {
                return;
            }

            _ = m_targets.Remove(key: key);
        }

        _ = target.Lifetime.Unregister();

        if ((target.Mirror is { } mirror) && (target.Handler is { } handler)) {
            mirror.DocumentDelivered -= handler;
        }

        foreach (var edit in target.Queue.Retire()) {
            echoes.Publish(result: CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{target.Name}' abandoned: {reason}]"));
        }
    }
    // Validates a composed row and composes its upsert under the issuing principal: a refused edit never enters the
    // queue. An edit that goes out now (rather than queuing behind one in flight) is refused while another door holds its
    // row in this tick's window.
    private static WorldEditorEditQueue.Edit? ComposeEdit(CommandContext context, EditWorld world, WorldPlacement row, string verb, bool queues, out CommandResult refusal) {
        if (!WorldDefinitionValidator.TryValidatePlacementGeometry(placement: row, reason: out var invalid)) {
            refusal = CommandResult.Error(output: $"[{verb}: '{row.Id}' in '{world.Name}' refused: {invalid}]");

            return null;
        }

        if (
            !queues &&
            (world.Guard?.IsClaimed(rowIdentity: WorldRowCommandModule.RowIdentityOf(key: row.Id, path: PlacementsPath), window: world.Window) is true)
        ) {
            refusal = CommandResult.Error(output: $"[{verb}: '{row.Id}' already has an edit buffered this tick in '{world.Name}'; fence with world.wait]");

            return null;
        }

        if (!WorldRowCommandModule.TryComposeRoutedSet(
            error: out var reason,
            json: JsonSerializer.Serialize(value: row, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement),
            mutation: out var mutation,
            path: PlacementsPath,
            principal: context.Principal
        )) {
            refusal = CommandResult.Error(output: $"[{verb}: '{row.Id}': {reason}]");

            return null;
        }

        refusal = CommandResult.None;

        return new WorldEditorEditQueue.Edit(Mutation: mutation!, Row: row, Verb: verb);
    }
    // Carries out what an offer admitted: a submitted edit goes to the world's link, a queued one waits; either makes
    // the placement the seat's current one there.
    private CommandResult Admit(CommandContext context, EditWorld world, EditTarget target, WorldEditorEditQueue.Admission admission, int slot) {
        if (admission.Queued is { } queued) {
            seats.SetCurrent(placement: queued.Row.Id, slot: slot, world: world.Name);

            return Echo(context: context, pending: "queued", placement: queued.Row, slot: slot, verb: queued.Verb, world: world);
        }

        if (admission.Submitted is not { } submission) {
            return admission.Refusal;
        }

        var edit = submission.Edit;
        var submitted = Dispatch(submission: submission, target: target);

        if (submitted.IsError) {
            return submitted;
        }

        world.Guard?.Claim(rowIdentity: WorldRowCommandModule.RowIdentityOf(key: edit.Row.Id, path: PlacementsPath));
        seats.SetCurrent(placement: edit.Row.Id, slot: slot, world: world.Name);

        return Echo(context: context, pending: "submitted", placement: edit.Row, slot: slot, verb: edit.Verb, world: world);
    }
    // Hands a submission to its world's link. Its verdict settles it, on whatever thread the link completes on, inline
    // or later; a link that throws settles it refused, so no submission stays in flight without a verdict.
    private CommandResult Dispatch(EditTarget target, WorldEditorEditQueue.Submission submission) {
        var edit = submission.Edit;

        try {
            return target.Link.Submit(
                echoes: echoes,
                mutation: edit.Mutation,
                observe: result => Conclude(result: result, submission: submission, target: target),
                verb: edit.Verb
            );
        } catch (Exception exception) {
            Conclude(result: null, submission: submission, target: target);

            return CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{target.Name}' was not submitted: {exception.Message}]");
        }
    }
    // Settles a submission with its verdict (null for a submission that never reached the link): applied, the document
    // this host already holds releases what it reflects and the queued edit goes next; refused, the rollback is named. A
    // settlement for a submission no longer in flight changes nothing.
    private void Conclude(EditTarget target, WorldEditorEditQueue.Submission submission, WorldSubmissionResult? result) {
        var edit = submission.Edit;
        var applied = (result is WorldSubmissionResult.Mutation { Outcome.Applied: true });
        var settlement = target.Queue.Settle(
            applied: applied,
            id: edit.Row.Id,
            token: submission.Token,
            version: ((result is WorldSubmissionResult.Mutation mutation) ? mutation.Outcome.Version : default)
        );

        if (applied) {
            _ = target.Queue.Deliver(version: target.Delivered().Version);
        }

        if (settlement.RolledBack) {
            var to = (settlement.RolledBackTo ?? WorldDefinitionRows.FindPlacement(id: edit.Row.Id, placements: target.Delivered().Definition.Placements));
            var position = ((to is { } row) ? Format(value: ((Vector3)row.Position)) : "nothing: it is not placed");
            var dropped = ((settlement.Dropped > 0) ? $", dropping {settlement.Dropped} queued edit{((settlement.Dropped == 1) ? string.Empty : "s")}" : string.Empty);

            echoes.Publish(result: CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{target.Name}' rolled back to {position}{dropped}]"));
        }

        if (settlement.Next is not { } next) {
            return;
        }

        var submitted = Dispatch(submission: next, target: target);

        if (!string.IsNullOrEmpty(value: submitted.Output)) {
            echoes.Publish(result: submitted);
        }
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
