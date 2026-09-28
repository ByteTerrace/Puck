using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

// Edits to placements run through one WorldEditorEditQueue per world activation, which owns their order, their base,
// their dispatch and their settlement. Every endpoint that reaches the world attaches to its queue; each edit carries its
// own endpoint's link. An endpoint detaches when it closes (its instance stops or is reaped, it is disposed) or starts
// delivering another activation's documents (a crossing onward, a recreated world): the edits that go out through it,
// with everything queued behind them, are abandoned by name. The queue retires, and keeps nothing, when its last
// endpoint detaches.
public sealed partial class WorldEditorCommandModule {
    private readonly Lock m_targetGate = new();
    private readonly Dictionary<(string Authority, Guid Activation), EditTarget> m_targets = [];

    // One world activation's queue and the endpoints attached to it, each with the registrations that detach it.
    private sealed class EditTarget {
        public Dictionary<IServerLink, Attachment> Attachments { get; } = new(comparer: ReferenceEqualityComparer.Instance);
        public WorldEditorEditQueue Queue { get; set; } = null!;
    }
    private sealed class Attachment {
        public Action<WorldDeliveredDocument>? Handler { get; set; }
        public CancellationTokenRegistration Lifetime { get; set; }
        public WorldSessionMirror? Mirror { get; init; }
    }

    // The queue of the world activation a verb edits, opened on first use, with the verb's endpoint attached to it. A
    // world no authority has delivered a document of yet is refused: nothing it shows could be edited.
    private bool TryTargetOf(EditWorld world, string verb, out EditTarget target, out WorldEditorEditQueue.Source source, out CommandResult refusal) {
        target = null!;
        source = new WorldEditorEditQueue.Source(Delivered: world.Delivered, Link: world.Link, World: world.Name);
        refusal = CommandResult.None;

        if (!world.Version.IsDelivered) {
            refusal = CommandResult.Error(output: $"[{verb}: '{world.Name}' has delivered no document yet]");

            return false;
        }

        var key = (world.Authority, world.Version.Activation);

        lock (m_targetGate) {
            if (!m_targets.TryGetValue(key: key, value: out var existing)) {
                var opened = new EditTarget();

                opened.Queue = new WorldEditorEditQueue(
                    activation: world.Version.Activation,
                    send: submission => Send(submission: submission, target: opened)
                );
                existing = opened;
                m_targets[key] = existing;
            }

            target = existing;

            if (target.Attachments.ContainsKey(key: world.Link)) {
                return true;
            }

            var attaching = target;
            var link = world.Link;
            var attachment = new Attachment { Mirror = world.Mirror };

            target.Attachments[link] = attachment;

            if (world.Mirror is { } mirror) {
                attachment.Handler = document => {
                    if (!attaching.Queue.Deliver(document: document)) {
                        Detach(key: key, link: link, reason: "its link now delivers another world", target: attaching);
                    }
                };
                mirror.DocumentDelivered += attachment.Handler;
            }

            attachment.Lifetime = world.Lifetime.Register(callback: () => Detach(key: key, link: link, reason: "its link closed", target: attaching));
        }

        return true;
    }
    // Detaches one endpoint from a world's queue: the edits that go out through it, with everything queued behind them,
    // are abandoned by name; the last endpoint to go retires the queue. Total and idempotent, since it runs inside a
    // disposal or a delivery.
    private void Detach((string Authority, Guid Activation) key, EditTarget target, IServerLink link, string reason) {
        Attachment? attachment;
        bool last;

        lock (m_targetGate) {
            if (!target.Attachments.Remove(key: link, value: out attachment)) {
                return;
            }

            last = (target.Attachments.Count == 0);

            if (last && m_targets.TryGetValue(key: key, value: out var current) && ReferenceEquals(objA: current, objB: target)) {
                _ = m_targets.Remove(key: key);
            }
        }

        _ = attachment.Lifetime.Unregister();

        if ((attachment.Mirror is { } mirror) && (attachment.Handler is { } handler)) {
            mirror.DocumentDelivered -= handler;
        }

        foreach (var edit in (last ? target.Queue.Retire() : target.Queue.Abandon(link: link))) {
            echoes.Publish(result: CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{edit.Source.World}' abandoned: {reason}]"));
        }
    }
    // Validates a composed row and composes its upsert under the issuing principal: a refused edit never enters the
    // queue. An edit that goes out now (rather than queuing behind one in flight) is refused while another door holds its
    // row in the world's current window, and claims that row once it is handed to its link.
    private static WorldEditorEditQueue.Edit? ComposeEdit(CommandContext context, EditWorld world, WorldEditorEditQueue.Source source, WorldPlacement row, string verb, bool queues, out CommandResult refusal) {
        if (!WorldDefinitionValidator.TryValidatePlacementGeometry(placement: row, reason: out var invalid)) {
            refusal = CommandResult.Error(output: $"[{verb}: '{row.Id}' in '{world.Name}' refused: {invalid}]");

            return null;
        }

        var identity = WorldRowCommandModule.RowIdentityOf(key: row.Id, path: PlacementsPath);

        if (!queues && (world.Guard is { } guard) && guard.Guard.IsClaimed(rowIdentity: identity, window: guard.Window())) {
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

        return new WorldEditorEditQueue.Edit(
            Mutation: mutation!,
            Row: row,
            Sent: ((world.Guard is { } claims) ? () => claims.Guard.Claim(rowIdentity: identity, window: claims.Window()) : null),
            Source: source,
            Verb: verb
        );
    }
    // Carries out what an offer admitted: the placement becomes the seat's current one there.
    private CommandResult Admit(CommandContext context, EditWorld world, WorldEditorEditQueue.Admission admission, int slot) {
        if (admission.Admitted is not { } edit) {
            return admission.Result;
        }

        if (!admission.Queued && admission.Result.IsError) {
            return admission.Result;
        }

        seats.SetCurrent(placement: edit.Row.Id, slot: slot, world: world.Name);

        return Echo(context: context, pending: (admission.Queued ? "queued" : "submitted"), placement: edit.Row, slot: slot, verb: edit.Verb, world: world);
    }
    // The queue's sender: hands a submission to its source's link, under the queue's lock. Its verdict settles it, on
    // whatever thread the link completes on, inline or later; a link that throws settles it refused, so no submission
    // stays in flight without a verdict.
    private CommandResult Send(EditTarget target, WorldEditorEditQueue.Submission submission) {
        var edit = submission.Edit;

        try {
            var submitted = edit.Source.Link.Submit(
                expectedActivation: target.Queue.Activation,
                echoes: echoes,
                mutation: edit.Mutation,
                observe: result => Conclude(result: result, submission: submission, target: target),
                verb: edit.Verb
            );

            if (!submitted.IsError) {
                edit.Sent?.Invoke();
            }

            return submitted;
        } catch (Exception exception) {
            Report(edit: edit, settlement: target.Queue.Settle(applied: false, id: edit.Row.Id, token: submission.Token, version: default));

            return CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{edit.Source.World}' was not submitted: {exception.Message}]");
        }
    }
    // Settles a submission with its verdict. A settlement for a submission no longer in flight changes nothing.
    private void Conclude(EditTarget target, WorldEditorEditQueue.Submission submission, WorldSubmissionResult result) => Report(
        edit: submission.Edit,
        settlement: target.Queue.Settle(
            applied: (result is WorldSubmissionResult.Mutation { Outcome.Applied: true }),
            id: submission.Edit.Row.Id,
            token: submission.Token,
            version: ((result is WorldSubmissionResult.Mutation mutation) ? mutation.Outcome.Version : default)
        )
    );
    // Names what a settlement did: a rollback, the edits it abandoned, and what the link answered the next edit with.
    private void Report(WorldEditorEditQueue.Edit edit, WorldEditorEditQueue.Settlement settlement) {
        if (settlement.RolledBack) {
            var position = ((settlement.RolledBackTo is { } row) ? Format(value: ((Vector3)row.Position)) : "nothing: it is not placed");
            var dropped = ((settlement.Dropped > 0) ? $", dropping {settlement.Dropped} queued edit{((settlement.Dropped == 1) ? string.Empty : "s")}" : string.Empty);

            echoes.Publish(result: CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{edit.Source.World}' rolled back to {position}{dropped}]"));
        }

        foreach (var abandoned in settlement.Abandoned) {
            echoes.Publish(result: CommandResult.Error(output: $"[{abandoned.Verb}: '{abandoned.Row.Id}' in '{abandoned.Source.World}' abandoned: its link now delivers another world]"));
        }

        if (!string.IsNullOrEmpty(value: settlement.NextSent.Output)) {
            echoes.Publish(result: settlement.NextSent);
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
