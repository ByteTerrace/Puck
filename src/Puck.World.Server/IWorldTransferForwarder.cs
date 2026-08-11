using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The complete observable head of a committed traveler route. A forwarding chain returns the final
/// writer's address, clock, and pose in one answer so input and presentation can advance to the same authority
/// epoch without an inactive-body interval.</summary>
public readonly record struct WorldAuthorityRouteDescription(
    string Endpoint,
    WorldEntityAddress Entity,
    ulong Tick,
    FixedVector3 Position,
    FixedQuaternion Orientation,
    Vector3 BodyColor,
    byte Kit,
    byte Look,
    byte CatalogRig,
    string? PlacementId,
    WorldDefinition Definition
);

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

    /// <summary>Resolves the final observable authority epoch behind a departed peer generation.</summary>
    bool TryDescribeForwarding(WorldServer source, WorldPrincipal principal, out WorldAuthorityRouteDescription route, out string reason);
}
