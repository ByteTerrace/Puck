using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>The refusal code of a mutation composed on another world activation's document.</summary>
    public const string ActivationMismatchCode = "world.mutation.activation_mismatch";

    // Refuses, by name and before anything is recorded or applied, a mutation composed on the document of another world
    // activation than this one (a traveler link that reached this world before its sender saw it had): its base is not
    // this world's. A mutation that expects nothing is not checked.
    private WorldSubmissionResult? RefuseActivationMismatch(in SubmissionEnvelope envelope, WorldMutationBinding binding, Guid? expected) {
        var current = Host.DocumentVersion;

        if ((expected is not { } activation) || (activation == current.Activation)) {
            return null;
        }

        var reason = $"it was composed on world activation {activation}, and this world is activation {current.Activation}";

        if (envelope.Payload is WorldSubmissionPayload.Mutation mutation) {
            Reject(
                connectionId: envelope.ConnectionId,
                correlationId: envelope.CorrelationId,
                mutation: mutation.Value,
                reason: reason
            );
        }

        return new WorldSubmissionResult.Mutation(Outcome: WorldMutationOutcome.RefusedOutcome(
            binding: binding,
            code: ActivationMismatchCode,
            detail: reason,
            version: current
        ));
    }
}
