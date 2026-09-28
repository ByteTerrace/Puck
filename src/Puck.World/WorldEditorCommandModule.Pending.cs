using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

// A seat's edits to one placement run one at a time per world through WorldEditorEditQueue, which owns their order,
// their base value and their settlement. An edit is validated and composed under its own principal before it enters
// the queue, so a refused edit never does; the world's verdict, or a submission that throws, settles it through the
// one path below.
public sealed partial class WorldEditorCommandModule {
    private readonly WorldEditorEditQueue m_edits = new();

    // The row an edit to a placement is based on (WorldEditorEditQueue.Latest).
    private WorldPlacement? LatestOf(EditWorld world, string id) => m_edits.Latest(
        delivered: WorldDefinitionRows.FindPlacement(id: id, placements: world.Definition.Placements),
        id: id,
        world: world.Name
    );
    // Whether a new placement may take an id: none in the delivered document, and none an edit holds.
    private bool IsTaken(EditWorld world, string id) => (
        (WorldDefinitionRows.FindPlacement(id: id, placements: world.Definition.Placements) is not null) ||
        m_edits.IsReserved(id: id, world: world.Name)
    );
    // Validates and composes an edit under the issuing principal, then submits it, or queues it behind the one in
    // flight, and makes the placement the seat's current one there.
    private CommandResult SubmitPlacement(CommandContext context, EditWorld world, WorldPlacement placement, WorldPlacement? from, string verb, int slot) {
        if (!WorldDefinitionValidator.TryValidatePlacementGeometry(placement: placement, reason: out var invalid)) {
            return CommandResult.Error(output: $"[{verb}: '{placement.Id}' in '{world.Name}' refused: {invalid}]");
        }

        if (!TryCompose(context: context, mutation: out var mutation, placement: placement, refusal: out var refusal, verb: verb)) {
            return refusal;
        }

        var identity = WorldRowCommandModule.RowIdentityOf(key: placement.Id, path: PlacementsPath);

        if (
            !m_edits.IsInFlight(id: placement.Id, world: world.Name) &&
            (world.Guard?.IsClaimed(rowIdentity: identity, window: world.Window) is true)
        ) {
            return CommandResult.Error(output: $"[{verb}: '{placement.Id}' already has an edit buffered this tick in '{world.Name}'; fence with world.wait]");
        }

        if (m_edits.Offer(edit: new WorldEditorEditQueue.Edit(Mutation: mutation, Row: placement, Verb: verb, World: world.Name), origin: from) is not { } submission) {
            seats.SetCurrent(placement: placement.Id, slot: slot, world: world.Name);

            return Echo(context: context, pending: "queued", placement: placement, slot: slot, verb: verb, world: world);
        }

        var submitted = Dispatch(submission: submission, world: world);

        if (submitted.IsError) {
            return submitted;
        }

        world.Guard?.Claim(rowIdentity: identity);
        seats.SetCurrent(placement: placement.Id, slot: slot, world: world.Name);

        return Echo(context: context, pending: "submitted", placement: placement, slot: slot, verb: verb, world: world);
    }
    // Hands a submission to its world's link. Its verdict settles it, on whatever thread the link completes on, inline
    // or later; a link that throws settles it refused, so no submission stays in flight without a verdict.
    private CommandResult Dispatch(EditWorld world, WorldEditorEditQueue.Submission submission) {
        var edit = submission.Edit;

        try {
            return world.Link.Submit(
                echoes: echoes,
                mutation: edit.Mutation,
                observe: result => Conclude(applied: (result is WorldSubmissionResult.Mutation { Outcome.Applied: true }), submission: submission, world: world),
                verb: edit.Verb
            );
        } catch (Exception exception) {
            Conclude(applied: false, submission: submission, world: world);

            return CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{world.Name}' was not submitted: {exception.Message}]");
        }
    }
    // Settles a submission: refused, names the rollback; applied, submits the edit queued next. A settlement for a
    // submission no longer in flight changes nothing.
    private void Conclude(EditWorld world, WorldEditorEditQueue.Submission submission, bool applied) {
        var edit = submission.Edit;
        var settlement = m_edits.Settle(applied: applied, id: edit.Row.Id, token: submission.Token, world: world.Name);

        if (settlement.RolledBack) {
            var to = ((settlement.RolledBackTo is { } row) ? Format(value: ((Vector3)row.Position)) : "nothing: it was never placed");
            var dropped = ((settlement.Dropped > 0) ? $", dropping {settlement.Dropped} queued edit{((settlement.Dropped == 1) ? string.Empty : "s")}" : string.Empty);

            echoes.Publish(result: CommandResult.Error(output: $"[{edit.Verb}: '{edit.Row.Id}' in '{world.Name}' rolled back to {to}{dropped}]"));
        }

        if (settlement.Next is not { } next) {
            return;
        }

        var submitted = Dispatch(submission: next, world: world);

        if (!string.IsNullOrEmpty(value: submitted.Output)) {
            echoes.Publish(result: submitted);
        }
    }
    private static bool TryCompose(CommandContext context, WorldPlacement placement, string verb, out WorldMutation mutation, out CommandResult refusal) {
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
