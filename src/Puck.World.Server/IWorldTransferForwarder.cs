using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The composition-root route retained when a federated peer leaves this authority for another one. An older
/// transfer credential remains a durable route to the same traveler: the authority that issued that credential
/// forwards input and submissions to the traveler's next committed authority instead of leaving a dead body index.
/// </summary>
public interface IWorldTransferForwarder {
    /// <summary>Forwards one intent addressed to a departed peer generation.</summary>
    bool TryForwardIntent(WorldServer source, WorldPrincipal principal, in IntentSubmission submission, out string reason);

    /// <summary>Forwards one typed submission addressed to a departed peer generation.</summary>
    bool TryForwardSubmission(WorldServer source, WorldPrincipal principal, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason);

    /// <summary>Resolves the final observable authority/body behind a departed peer generation.</summary>
    bool TryDescribeForwarding(WorldServer source, WorldPrincipal principal, out string endpoint, out int bodyIndex, out string reason);
}
