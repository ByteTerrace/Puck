using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The composition-root route retained when a federated peer leaves this authority for another one. An older
/// transfer credential remains a durable route to the same traveler: the authority that issued that credential
/// forwards input and submissions to the traveler's next committed authority instead of leaving a dead body index.
/// </summary>
public interface IWorldTransferForwarder {
    /// <summary>Names host-owned transfer state that prevents this authority from rewinding independently, or
    /// returns null when it has none. Called on the host's tick thread while holding the source authority gate.</summary>
    string? TimelineResetRefusal(WorldServer source);
    /// <summary>Resolves already-evaluated adjacency continuations before this authority advances its population.
    /// The caller already holds <paramref name="source"/>'s authority gate.</summary>
    void ResolveContinuations(WorldServer source);
    /// <summary>Forwards one intent addressed to a departed traveler incarnation.</summary>
    bool TryForwardIntent(WorldServer source, in WorldMobilityIdentity mobility, in IntentSubmission submission, out string reason);
    /// <summary>Forwards one typed submission addressed to a departed traveler incarnation.</summary>
    bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason);
    /// <summary>Forwards a typed submission while preserving its caller operation id.</summary>
    bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, out WorldSubmissionResult? result, out string reason) {
        if (operationId == Guid.Empty && payload is WorldSubmissionPayload.Mutation) {
            result = new WorldSubmissionResult.Refusal("world.mutation.operation_id_missing", "mutation operation id is required");
            reason = "mutation operation id is required";
            return false;
        }
        return TryForwardSubmission(source, in mobility, payload, out result, out reason);
    }
    /// <summary>Resolves the final observable authority epoch behind a departed traveler incarnation.</summary>
    bool TryDescribeForwarding(WorldServer source, in WorldMobilityIdentity mobility, out WorldAuthorityRouteDescription route, out string reason);
    /// <summary>Streams the current owner's projection for an already authenticated departed traveler.</summary>
    /// <param name="source">The authority whose committed onward route is followed.</param>
    /// <param name="request">The credential and remaining disclosure/work bounds.</param>
    /// <param name="output">The caller-owned downstream stream.</param>
    /// <param name="ct">The observation lifetime cancellation.</param>
    /// <returns>A refusal before streaming, or null after the stream ends.</returns>
    Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct);
}
