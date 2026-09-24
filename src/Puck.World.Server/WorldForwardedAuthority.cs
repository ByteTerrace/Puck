using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Mints the lease ids that own a federated held-input stream on a destination server.</summary>
/// <remarks>Process-wide, because a lease is identified to <see cref="WorldServer.PublishFederatedIntent"/> by that
/// id alone: two minters feeding one server must not be able to choose the same number, or one lane's release would
/// silence the other's held state.</remarks>
public static class WorldFederatedIntentLease {
    private static long NextLeaseId;

    /// <summary>Returns a lease id no other holder will be given.</summary>
    /// <returns>The lease id.</returns>
    public static long Next() => Interlocked.Increment(location: ref NextLeaseId);
}
/// <summary>One authority a departed traveler's acts are forwarded to, whether it is hosted in this process or
/// reached over a socket. A caller that resolves a traveler's onward route holds one of these and never branches on
/// where that authority lives.</summary>
public interface IWorldForwardedAuthority {
    /// <summary>Forwards one per-tick intent image to the traveler's current authority.</summary>
    /// <param name="submission">The intent image; its entity index is rebound by the implementation.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the intent was accepted.</returns>
    bool TryForwardIntent(in IntentSubmission submission, out string reason);
    /// <summary>Forwards one typed submission to the traveler's current authority.</summary>
    /// <param name="payload">The submission payload, already rebound to the destination body index.</param>
    /// <param name="result">The typed completion on success.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the submission reached a typed result.</returns>
    bool TryForwardSubmission(WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason);
    /// <summary>Forwards a submission while preserving its caller operation id.</summary>
    bool TryForwardSubmission(WorldSubmissionPayload payload, Guid operationId, out WorldSubmissionResult? result, out string reason) {
        if (
            (operationId == Guid.Empty) &&
            (payload is WorldSubmissionPayload.Mutation)
        ) {
            result = new WorldSubmissionResult.Refusal(
                Code: "world.mutation.operation_id_missing",
                Detail: "mutation operation id is required"
            );
            reason = "mutation operation id is required";
            return false;
        }
        return TryForwardSubmission(
            payload: payload,
            reason: out reason,
            result: out result
        );
    }
    /// <summary>Resolves the traveler's current observable authority epoch.</summary>
    /// <param name="route">The route description on success.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when a route was described.</returns>
    bool TryDescribeRoute(out WorldAuthorityRouteDescription route, out string reason);
    /// <summary>Returns the destination hop and source-scoped credential without network I/O. Remote hops also
    /// carry their endpoint and a definition for reconnecting; no live stream or held-input lease is persisted.</summary>
    /// <returns>The destination descriptor, independent of connection availability.</returns>
    WorldForwardingDestination DescribeForCheckpoint();
    /// <summary>Streams the current owner's projection through this authenticated hop without closing the output stream.</summary>
    /// <param name="output">The caller-owned downstream stream.</param>
    /// <param name="ceiling">The maximum document disclosure admitted upstream.</param>
    /// <param name="remainingHops">The remaining forwarding work bound.</param>
    /// <param name="ct">The observation lifetime cancellation.</param>
    /// <returns>A refusal before streaming, or null after the stream ends.</returns>
    Task<string?> StreamProjectionAsync(Stream output, WorldDisclosureTier ceiling, byte remainingHops, CancellationToken ct);
}
/// <summary>The data needed to rebind one forwarding hop. Endpoint and Definition are both null for a local hop;
/// otherwise they seed a new remote connection whose handshake must prove DestinationAuthority.</summary>
/// <param name="DestinationAuthority">The exact destination authority identity, not its host registry name.</param>
/// <param name="SourceAuthority">The authenticated source namespace that minted the credential.</param>
/// <param name="Mobility">The traveler's incarnation and committed ownership epoch.</param>
/// <param name="Endpoint">The remote IP endpoint, or null for a local-only hop.</param>
/// <param name="Definition">The remote definition used to reconnect, or null for a local-only hop.</param>
public sealed record WorldForwardingDestination(string DestinationAuthority, string SourceAuthority,
    WorldMobilityIdentity Mobility, string? Endpoint = null, WorldDefinition? Definition = null);
/// <summary>
/// The forwarded-authority arm for a traveler whose current authority is a <see cref="WorldServer"/> in this
/// process. It is the same act a federated peer performs over a socket, minus the socket: the credential still
/// names the body, the acting principal still comes from the destination's own transfer table, and the destination's
/// grants still decide.
/// </summary>
/// <remarks>
/// <para>Callers arrive on a socket worker or on another authority's tick thread, never necessarily on this
/// server's. Every population read and the act it authorizes therefore run inside ONE
/// <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>: split across two, a body that detaches in between would
/// let a submission apply to a slot its traveler no longer owns.</para>
/// <para>The instance owns a held-input lease for as long as it is a traveler's route. Replacing or dropping the
/// route MUST <see cref="Dispose"/> it, or the destination keeps republishing the last image it was handed.</para>
/// <para>A credential that still authenticates but no longer owns a live body follows the destination's committed
/// onward route. Forwarding never holds one authority gate while entering the next. Synchronous local traversal
/// refuses past 64 hops to bound stack use; an accepted leave retires the credential on every visited hop.</para>
/// </remarks>
public sealed class WorldLocalForwardedAuthority : IWorldForwardedAuthority, IDisposable {
    private readonly string m_endpoint;
    private readonly long m_leaseId;
    private readonly WorldMobilityIdentity m_mobility;
    private readonly WorldServer m_server;
    private readonly string m_sourceAuthority;

    private int m_disposed;

    /// <summary>Initializes a forwarded arm over a colocated destination server.</summary>
    /// <param name="server">The destination authority.</param>
    /// <param name="endpoint">The endpoint text a route description reports for that authority.</param>
    /// <param name="sourceAuthority">The authenticated namespace that minted the traveler's credential.</param>
    /// <param name="mobility">The traveler's incarnation and committed ownership epoch.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldLocalForwardedAuthority(WorldServer server, string endpoint, string sourceAuthority, in WorldMobilityIdentity mobility) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceAuthority);

        m_server = server;
        m_endpoint = endpoint;
        m_sourceAuthority = sourceAuthority;
        m_mobility = mobility;
        m_leaseId = WorldFederatedIntentLease.Next();
    }

    /// <summary>Gets the destination authority this arm forwards to.</summary>
    public WorldServer Server => m_server;

    private bool TryDescribeRouteCore(out WorldAuthorityRouteDescription route, out string reason) {
        route = default;

        if (!TryResolvePrincipal(
            principal: out var principal,
            reason: out reason
        )) {
            return false;
        }

        var described = m_server.ExecuteAuthorityOperation(operation: () =>
            (IsLiveTransferredPrincipal(
            principal: principal,
            server: m_server
        )
            ? DescribeRoute(
                endpoint: m_endpoint,
                principal: principal,
                server: m_server
            )
            : (WorldAuthorityRouteDescription?)null));

        if (described is not { } resolved) {
            if (m_server.TransferForwarder is { } forwarder) {
                return forwarder.TryDescribeForwarding(
                    mobility: in m_mobility,
                    reason: out reason,
                    route: out route,
                    source: m_server
                );
            }
            reason = "the traveler is no longer live at this authority";

            return false;
        }

        route = resolved;
        reason = string.Empty;

        return true;
    }
    private bool TryForwardIntentCore(in IntentSubmission submission, out string reason) {
        if (!TryResolvePrincipal(
            principal: out var principal,
            reason: out reason
        )) {
            return false;
        }

        var stamped = submission with { EntityIndex = principal.Index, Principal = principal };
        var accepted = m_server.ExecuteAuthorityOperation(operation: () => {
            if (Volatile.Read(location: ref m_disposed) != 0) { return (Accepted: false, Closed: true); }
            if (!IsLiveTransferredPrincipal(
                principal: principal,
                server: m_server
            )) {
                return (Accepted: false, Closed: false);
            }

            m_server.PublishFederatedIntent(
                leaseId: m_leaseId,
                submission: in stamped
            );

            return (Accepted: true, Closed: false);
        });

        // Never hold one authority's operation gate while calling the next authority.
        if (
            !accepted.Accepted &&
            !accepted.Closed &&
            (m_server.TransferForwarder is { } forwarder)
        ) {
            return forwarder.TryForwardIntent(
                mobility: in m_mobility,
                reason: out reason,
                source: m_server,
                submission: in stamped
            );
        }
        reason = (accepted.Accepted
            ? string.Empty
            : "the forwarding lease is closed or the traveler is no longer live at this authority"
        );

        return accepted.Accepted;
    }
    private bool TryResolvePrincipal(out Principal principal, out string reason) {
        if (!m_server.TryTransferredPrincipal(
            mobility: in m_mobility,
            principal: out principal,
            sourceAuthority: m_sourceAuthority
        )) {
            reason = $"traveler {m_mobility.Incarnation} has no committed credential at this authority";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <inheritdoc/>
    public WorldForwardingDestination DescribeForCheckpoint() => new(
        m_server.AuthorityIdentity,
        m_sourceAuthority,
        m_mobility
    );
    /// <summary>Describes one live body's complete observable authority epoch. MUST be called inside
    /// <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>.</summary>
    /// <param name="server">The authority holding the body.</param>
    /// <param name="endpoint">The endpoint text to report.</param>
    /// <param name="principal">The body's owning principal.</param>
    /// <returns>The route description.</returns>
    /// <exception cref="InvalidOperationException">The principal names no body.</exception>
    public static WorldAuthorityRouteDescription DescribeRoute(WorldServer server, string endpoint, Principal principal) =>
        DescribeRoute(
            server: server,
            endpoint: endpoint,
            bodyIndex: principal.Index
        );
    /// <summary>Describes one live body's complete observable authority epoch by index.</summary>
    /// <remarks>Callers hold <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>. A presentation route seeds from
    /// this before publishing the route, so no interval exists in which a consumer holds the route and no
    /// anchor.</remarks>
    /// <param name="server">The authority holding the body.</param>
    /// <param name="endpoint">The endpoint text to report.</param>
    /// <param name="bodyIndex">The body index.</param>
    /// <returns>The route description.</returns>
    /// <exception cref="InvalidOperationException">The index names no body.</exception>
    public static WorldAuthorityRouteDescription DescribeRoute(WorldServer server, string endpoint, int bodyIndex) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var body = (server.Population.EntryBody(index: bodyIndex) ??
            throw new InvalidOperationException(message: $"body:{bodyIndex} at '{server.AuthorityIdentity}' has no body to describe"));

        return new WorldAuthorityRouteDescription(
            Endpoint: endpoint,
            Entity: new WorldEntityAddress(
                Authority: server.AuthorityIdentity,
                Index: bodyIndex,
                Generation: server.Population.Generation(index: bodyIndex)
            ),
            Tick: (server.NextInputTick - 1UL),
            Position: body.FixedPosition,
            Orientation: body.FixedOrientation,
            BodyColor: server.Population.BodyColor(index: bodyIndex),
            Kit: server.Population.KitIndex(index: bodyIndex),
            Look: server.Population.LookIndex(index: bodyIndex),
            CatalogRig: server.Population.CatalogRig(index: bodyIndex),
            PlacementId: server.Population.InhabitantPlacementId(index: bodyIndex),
            Definition: server.Definition
        );
    }
    /// <summary>Releases the held-input lease this arm owns and refuses further intent publication. Release and
    /// publication are serialized by the destination's authority gate. Retired destinations remain frozen.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0) { m_server.ReleaseFederatedIntents(leaseId: m_leaseId); }
    }
    /// <summary>Reports whether a transferred principal still owns its body. MUST be called inside
    /// <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>, paired with the act it authorizes.</summary>
    /// <param name="server">The authority holding the population.</param>
    /// <param name="principal">The principal to test.</param>
    /// <returns><see langword="true"/> when the principal is the live owner of its body index.</returns>
    public static bool IsLiveTransferredPrincipal(WorldServer server, Principal principal) {
        ArgumentNullException.ThrowIfNull(argument: server);

        return (
            (principal.Kind == PrincipalKind.Peer) &&
            (((uint)principal.Index) < ((uint)server.Population.Capacity)) &&
            server.Population.IsActive(index: principal.Index) &&
            (server.Population.PeerPrincipal(index: principal.Index) == principal)
        );
    }
    /// <summary>Stamps a payload's embedded principal with the identity the door resolved.</summary>
    /// <param name="payload">The decoded payload.</param>
    /// <param name="principal">The acting principal.</param>
    /// <returns>The payload carrying <paramref name="principal"/>.</returns>
    public static WorldSubmissionPayload StampPrincipal(WorldSubmissionPayload payload, Principal principal) => payload switch {
        WorldSubmissionPayload.Command command => new WorldSubmissionPayload.Command(Value: (command.Value with { Principal = principal })),
        WorldSubmissionPayload.Session session => new WorldSubmissionPayload.Session(Value: (session.Value with { Principal = principal })),
        WorldSubmissionPayload.Mutation mutation => new WorldSubmissionPayload.Mutation(Value: (mutation.Value with { Principal = principal })),
        _ => payload,
    };
    /// <inheritdoc/>
    public Task<string?> StreamProjectionAsync(Stream output, WorldDisclosureTier ceiling, byte remainingHops, CancellationToken ct) =>
        WorldTravelerProjection.StreamAsync(
            m_server,
            new(
                Ceiling: ceiling,
                Mobility: m_mobility,
                RemainingHops: remainingHops,
                SourceAuthority: m_sourceAuthority
            ),
            m_endpoint,
            output,
            ct
        );
    /// <summary>Applies one already-decoded submission to a live transferred body, resolving the acting principal
    /// from the destination's own transfer table. Runs the liveness test and the act it authorizes as one gated
    /// operation.</summary>
    /// <param name="server">The destination authority.</param>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="payload">The submission payload.</param>
    /// <param name="result">The typed completion when the body was live.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the body was live here; <see langword="false"/> leaves the caller to
    /// follow the traveler's onward route.</returns>
    public static bool TryApplySubmission(WorldServer server, string sourceAuthority, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) =>
        TryApplySubmission(
            mobility: in mobility,
            operationId: Guid.Empty,
            payload: payload,
            reason: out reason,
            result: out result,
            server: server,
            sourceAuthority: sourceAuthority
        );
    public static bool TryApplySubmission(WorldServer server, string sourceAuthority, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, out WorldSubmissionResult? result, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: server);

        result = null;

        if (!server.TryTransferredPrincipal(
            mobility: in mobility,
            principal: out var principal,
            sourceAuthority: sourceAuthority
        )) {
            reason = "the credential names no committed transfer body";

            return false;
        }

        var applied = server.ExecuteAuthorityOperation(operation: () => {
            if (!IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            )) {
                return (Live: false, Result: ((WorldSubmissionResult?)null));
            }

            if (
                (payload is WorldSubmissionPayload.Mutation mutation) &&
                (mutation.Value.Principal != principal)
            ) {
                return (Live: true, Result: ((WorldSubmissionResult?)new WorldSubmissionResult.Refusal(
                    Code: "world.mutation.actor_mismatch",
                    Detail: "mutation actor does not match the authenticated transferred principal"
                )));
            }
            var stamped = StampPrincipal(
                payload: payload,
                principal: principal
            );

            if (
                (stamped is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
                server.Population.TryCaptureTransferredPeer(
                index: principal.Index,
                peer: out var peer
            )
            ) {
                server.DisconnectPeerConnection(peer: peer);

                return (Live: true, Result: ((WorldSubmissionResult?)new WorldSubmissionResult.Session(Reply: new SessionReply(
                    Accepted: true,
                    AssignedIndex: (principal.Index + 1),
                    RosterEcho: string.Empty,
                    Reason: string.Empty
                ))));
            }

            WorldSubmissionResult? captured = null;

            server.Submit(
                envelope: new SubmissionEnvelope(
                    ConnectionId: principal.Index,
                    SessionGeneration: principal.Generation,
                    Sequence: 0,
                    CorrelationId: 0,
                    Principal: principal,
                    Payload: stamped,
                    OperationId: operationId
                ),
                completion: value => captured = value
            );

            return (Live: true, Result: captured);
        });

        if (!applied.Live) {
            reason = "the traveler is no longer live at this authority";

            return false;
        }

        result = applied.Result;
        reason = string.Empty;

        return true;
    }
    /// <inheritdoc/>
    public bool TryDescribeRoute(out WorldAuthorityRouteDescription route, out string reason) {
        if (!WorldForwardingScope.TryEnter(
            reason: out reason,
            scope: out var scope
        )) { route = default; return false; }
        using (scope) {
            return TryDescribeRouteCore(
            reason: out reason,
            route: out route
        );
        }
    }
    /// <inheritdoc/>
    public bool TryForwardIntent(in IntentSubmission submission, out string reason) {
        if (!WorldForwardingScope.TryEnter(
            reason: out reason,
            scope: out var scope
        )) { return false; }
        using (scope) {
            return TryForwardIntentCore(
            reason: out reason,
            submission: in submission
        );
        }
    }
    /// <inheritdoc/>
    public bool TryForwardSubmission(WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) => TryForwardSubmission(
        operationId: Guid.Empty,
        payload: payload,
        reason: out reason,
        result: out result
    );
    /// <inheritdoc/>
    public bool TryForwardSubmission(WorldSubmissionPayload payload, Guid operationId, out WorldSubmissionResult? result, out string reason) {
        result = null;
        if (!WorldForwardingScope.TryEnter(
            reason: out reason,
            scope: out var scope
        )) { return false; }
        using (scope) {
            // A missing credential must not become permission to follow a known incarnation's route.
            if (!TryResolvePrincipal(
                principal: out _,
                reason: out reason
            )) { return false; }
            var accepted = TryApplySubmission(
                mobility: in m_mobility,
                operationId: operationId,
                payload: payload,
                reason: out reason,
                result: out result,
                server: m_server,
                sourceAuthority: m_sourceAuthority
            );

            if (
                !accepted &&
                (m_server.TransferForwarder is { } forwarder)
            ) {
                accepted = forwarder.TryForwardSubmission(
                    mobility: in m_mobility,
                    operationId: operationId,
                    payload: payload,
                    reason: out reason,
                    result: out result,
                    source: m_server
                );
            }
            if (
                accepted &&
                (payload is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
                (result is WorldSubmissionResult.Session { Reply.Accepted: true })
            ) {
                m_server.RetireTransferredMobility(mobility: in m_mobility);
            }
            return accepted;
        }
    }
}
