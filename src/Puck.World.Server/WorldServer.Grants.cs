using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Raised on the tick thread whenever the live <c>admission</c> section is replaced, by an install, an
    /// undo, a rebuild or a checkpoint restore alike. It only wakes a holder that would otherwise not look again until
    /// its next frame or command: the policy itself is read live at every write, so a handler is never the only
    /// enforcement. A handler runs on the tick thread, so it must not block or cancel synchronously.</summary>
    public event Action? AdmissionChanged;
    /// <summary>Closes transports attached to a generation when its ordered disconnect applies.</summary>
    public event Action<Principal>? PeerDisconnected;

    // Each handler runs in its own try, so one that throws neither skips the handlers after it nor faults the install,
    // restore or seek that replaced the section.
    internal void NotifyAdmissionChanged() {
        if (AdmissionChanged is not { } handlers) { return; }
        foreach (var handler in handlers.GetInvocationList().Cast<Action>()) {
            try {
                handler();
            } catch (Exception fault) {
                m_output.Narrate(channel: "world.admission", text: $"[world.admission: a live admission handler faulted: {fault.GetType().Name}: {fault.Message}]");
            }
        }
    }
    internal void ClosePeerTransport(Principal principal) => PeerDisconnected?.Invoke(principal);

    /// <summary>Whether a peer generation is still connected and may submit commands.</summary>
    public bool IsLivePeer(Principal principal) => ((principal.Kind == PrincipalKind.Peer) && Population.IsAdmittedPeer(bodyIndex: principal.Index) &&
        !Population.IsParked(index: principal.Index) && (Population.PeerPrincipal(index: principal.Index) == principal));
    /// <summary>Disconnects one live peer through the ordered lifecycle, including its transports.</summary>
    public bool DisconnectPeer(Principal peer, Principal actor) {
        lock (AuthorityGate) {
            if (!Grants.IsOperator(principal: actor) || !IsLivePeer(principal: peer) || !Population.TryCaptureTransferredPeer(index: peer.Index, peer: out var entry)) { return false; }
            DisconnectPeerConnection(peer: entry);
            return true;
        }
    }

    /// <summary>Gets the mounted Simulation-lane addon host, or <see langword="null"/> when none is attached.</summary>
    public IWorldAddonHost? Addons { get { ThrowIfOutsideWindow(); return m_addons; } }
    /// <summary>Gets the concrete grant facade — the capability table plus the grant-application, ownership-escrow
    /// and peer/session admission halves that run against it.</summary>
    /// <remarks><see cref="Grants"/> remains the narrow <see cref="IWorldGrantsView"/> every ordinary reader holds;
    /// this property is the facade itself. Its direct grant/revoke doors require the actor and pass through the
    /// same authority transition admission as document installation.</remarks>
    public WorldGrants GrantTable { get { ThrowIfOutsideWindow(); return m_grants; } }
    /// <summary>Gets the per-tick dispatch tally behind the mutation admission budget gate.</summary>
    public WorldMutationBudgetMeter MutationBudget { get { ThrowIfOutsideWindow(); return m_mutationBudget; } }

    /// <inheritdoc cref="WorldGrants.ApplySession"/>
    public SessionReply ApplySession(SessionRequest request) => m_grants.ApplySession(request: request);
    /// <inheritdoc cref="WorldGrants.ApplySessionLever"/>
    public void ApplySessionLever(WorldSessionLever lever, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_grants.ApplySessionLever(
            connectionId: connectionId,
            correlationId: correlationId,
            lever: lever,
            principal: principal
        );
    /// <inheritdoc cref="WorldGrants.DisconnectPeerConnection"/>
    public void DisconnectPeerConnection(WorldPeerEventEntry peer) => m_grants.DisconnectPeerConnection(peer: peer);
    /// <inheritdoc cref="WorldGrants.ApplyGrant"/>
    public void Grant(WorldGrant grant, Principal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_grants.ApplyGrant(
            actor: actor,
            connectionId: connectionId,
            correlationId: correlationId,
            grant: grant
        );
    /// <summary>Returns the concrete grant rows held by one principal. Transfer rollback captures these before a
    /// federated peer generation leaves so an aborted onward handoff can restore the exact source authority.</summary>
    /// <param name="principal">The holder.</param>
    /// <returns>That principal's rows.</returns>
    public IReadOnlyList<WorldGrant> GrantRows(Principal principal) => m_grants.Rows(principal: principal);
    /// <inheritdoc cref="WorldGrants.ApplyRevoke"/>
    public void Revoke(WorldGrant grant, Principal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        m_grants.ApplyRevoke(
            actor: actor,
            connectionId: connectionId,
            correlationId: correlationId,
            grant: grant
        );
    /// <summary>Preflights every authority dependency of a mutation without consuming its dispatch budget.
    /// The ordered drain repeats admission and meters the actual submission.</summary>
    /// <param name="mutation">The mutation being composed by the caller.</param>
    /// <param name="admission">The complete authority verdict.</param>
    /// <returns>Whether the caller may compose this mutation.</returns>
    public bool TryAdmitCompleteMutation(WorldMutation mutation, out WorldMutationAdmission admission) =>
        m_document.TryAdmitCompleteMutation(admission: out admission, mutation: mutation, preMetered: true);
    /// <inheritdoc cref="WorldGrants.TryAdmitMutation"/>
    public bool TryAdmitMutation(Principal principal, WorldSection section, int kindOrdinal, GrantSubject? rowScopedEditSubject, GrantSubject? rowScopedMutateSubject, bool meter, out WorldMutationAdmission admission) =>
        m_grants.TryAdmitMutation(
            admission: out admission,
            kindOrdinal: kindOrdinal,
            meter: meter,
            principal: principal,
            rowScopedEditSubject: rowScopedEditSubject,
            rowScopedMutateSubject: rowScopedMutateSubject,
            section: section
        );
    /// <inheritdoc cref="WorldGrants.TryAdmitPeerConnection"/>
    public bool TryAdmitPeerConnection(WorldAdmissionVerdict? verdict, IReadOnlyList<WorldAdmissionEntry>? expectedAdmissionEntries, out WorldPeerEventEntry admitted, out string refusal) =>
        m_grants.TryAdmitPeerConnection(
            admitted: out admitted,
            expectedAdmissionEntries: expectedAdmissionEntries,
            refusal: out refusal,
            verdict: verdict
        );
}
