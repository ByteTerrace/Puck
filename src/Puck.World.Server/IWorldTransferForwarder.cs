using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The composition-root route retained when a federated peer leaves this authority for another one. An older
/// transfer credential remains a durable route to the same traveler: the authority that issued that credential
/// forwards input and submissions to the traveler's next committed authority instead of leaving a dead body index.
/// </summary>
public interface IWorldTransferForwarder {
    /// <summary>Resolves already-evaluated adjacency continuations before this authority advances its population.
    /// The caller already holds <paramref name="source"/>'s authority gate.</summary>
    void ResolveContinuations(WorldServer source);
    /// <summary>Forwards one intent addressed to a departed traveler incarnation.</summary>
    bool TryForwardIntent(WorldServer source, in WorldMobilityIdentity mobility, in IntentSubmission submission, out string reason);
    /// <summary>Forwards one typed submission addressed to a departed traveler incarnation.</summary>
    bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason);
    /// <summary>Resolves the final observable authority epoch behind a departed traveler incarnation.</summary>
    bool TryDescribeForwarding(WorldServer source, in WorldMobilityIdentity mobility, out WorldAuthorityRouteDescription route, out string reason);
}
