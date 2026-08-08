namespace Puck.World.Protocol;

/// <summary>
/// The one envelope shape every non-intent submission travels in — command, grant, revoke, session, definition,
/// mutation, undo, composition, lever, and query all ride the SAME record, never a per-kind wrapper, so the server's
/// ordered domain (<c>Server.WorldServer.Submit</c>) is one queue, not one queue per kind. Per-tick
/// <see cref="IntentSubmission"/> is NOT an envelope payload — intents keep their own separate buffer.
/// </summary>
/// <param name="ConnectionId">The originating connection — <c>0</c> is the local stdin/loopback connection, always.
/// A future socket transport assigns every accepted remote connection a nonzero id at admission.</param>
/// <param name="SessionGeneration">The connection's session generation — bumps on admission/reactivation (peer
/// identity's stale-generation scrub). Always <c>0</c> for the local connection, which never regenerates.</param>
/// <param name="Sequence">The PER-CONNECTION monotonic submission counter — the watermark unit a remote
/// <c>world.wait</c>/read-barrier arms against. Local submitters still get one (draining is synchronous, so it is
/// never consulted for backpressure), assigned by the transport that mints the envelope.</param>
/// <param name="CorrelationId">The token a completion (inline callback locally, a Completion frame remotely)
/// correlates back to this specific envelope — independent of <see cref="Sequence"/>, which orders arrival rather
/// than identifying one submission.</param>
/// <param name="Principal">The acting identity the envelope is checked against — CLAIMED by the submitter, and
/// (once a wire exists) validated against the connection's admitted principal set at the door before this envelope
/// ever reaches the ordered domain. The local transport stamps the identity its own ingress door already resolved.</param>
/// <param name="Payload">The closed submission-kind union (see <see cref="WorldSubmissionPayload"/>).</param>
public readonly record struct SubmissionEnvelope(
    int ConnectionId,
    int SessionGeneration,
    long Sequence,
    long CorrelationId,
    WorldPrincipal Principal,
    WorldSubmissionPayload Payload
) {
    /// <summary>The local stdin/loopback connection id — the only connection id that exists before a wire lands.</summary>
    public const int LocalConnectionId = 0;
}
