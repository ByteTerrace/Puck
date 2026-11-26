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
    /// <summary>Returns whether a committed onward route is published for a departed traveler incarnation. A traveler
    /// detaches before its route is published, so a request that lands in that interval finds none yet; a caller
    /// waits on this rather than reading it from a refusal's text. Safe to call from any thread.</summary>
    /// <param name="source">The authority whose committed onward route is followed.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <returns><see langword="true"/> when the route is published.</returns>
    bool HasOnwardRoute(WorldServer source, in WorldMobilityIdentity mobility);
    /// <summary>Tells the host that a traveler's body was retired at the authority that held it and went nowhere: its
    /// reconnect grace ran out, or it disconnected with none. Every authority of the host that still forwards to the
    /// traveler drops its route and its credential, as each does when an accepted leave's answer arrives. Called under
    /// the retiring authority's gate, so the host defers the work to its own loop.</summary>
    /// <param name="incarnation">The retired traveler's incarnation.</param>
    void RetireTraveler(WorldEntityAddress incarnation);
    /// <summary>Passes on whether a departed traveler's source still attends to it, to the authority this one forwards
    /// it to. A traveler that walked on keeps its credential at <paramref name="source"/> and its body further along, so
    /// the body parks when the source goes away and resumes when it returns, wherever it stands. Called under the
    /// source authority's gate, so the host defers the work to its own loop.</summary>
    /// <param name="source">The authority that holds the credential and no longer the body.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="present">Whether the source attends to the traveler.</param>
    void SetTravelerPresence(WorldServer source, in WorldMobilityIdentity mobility, bool present);
    /// <summary>Forwards one intent addressed to a departed traveler incarnation.</summary>
    bool TryForwardIntent(WorldServer source, in WorldMobilityIdentity mobility, in IntentSubmission submission, out string reason);
    /// <summary>Forwards one typed submission addressed to a departed traveler incarnation, preserving its caller
    /// operation id; its typed result reaches <paramref name="completion"/> exactly once when it reached an authority
    /// (see <see cref="IWorldForwardedAuthority.TryForwardSubmission"/>).</summary>
    /// <param name="source">The authority whose committed onward route is followed.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="payload">The submission payload.</param>
    /// <param name="operationId">The caller's operation id.</param>
    /// <param name="completion">Receives the typed result.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the submission reached an authority.</returns>
    bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason);
    /// <summary>Resolves the final observable authority epoch behind a departed traveler incarnation.</summary>
    ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeForwardingAsync(WorldServer source, WorldMobilityIdentity mobility);
    /// <summary>Fetches a prototype through the departed traveler's current projection.</summary>
    ValueTask<byte[]?> FetchForwardedPrototypeAsync(WorldServer source, WorldTravelerObservation request, Puck.Assets.ContentPin pin);
    /// <summary>Serves one segment of an already authenticated departed traveler's observation stream from the
    /// authority it stands at, along <paramref name="source"/>'s committed onward route.</summary>
    /// <param name="source">The authority whose committed onward route is followed.</param>
    /// <param name="request">The credential and remaining disclosure/work bounds.</param>
    /// <param name="session">The stream, writing through <paramref name="source"/>'s view of the connection.</param>
    /// <param name="ct">The observation lifetime cancellation.</param>
    /// <returns>How the segment came out.</returns>
    Task<WorldProjectionOutcome> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, WorldProjectionSession session, CancellationToken ct);
    /// <summary>Tells the host that a traveler landed at <paramref name="destination"/>: a route that authority kept
    /// from an earlier departure of the same traveler leads nowhere the traveler will stand, so the host drops it and
    /// nothing is forwarded along it again. Called under the landing authority's gate, so the host defers the work to
    /// its own loop.</summary>
    /// <param name="destination">The authority the traveler landed at.</param>
    /// <param name="mobility">The traveler's incarnation and the ownership epoch it landed with.</param>
    void TravelerLanded(WorldServer destination, in WorldMobilityIdentity mobility);
}
