using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the mounted Simulation-lane addon host, or <see langword="null"/> when none is attached.</summary>
    public IWorldAddonHost? Addons => m_addons;
    /// <summary>Gets the concrete grant facade — the capability table plus the grant-application, ownership-escrow
    /// and peer/session admission halves that run against it.</summary>
    /// <remarks><see cref="Grants"/> remains the narrow <see cref="IWorldGrantsView"/> every ordinary reader holds;
    /// this property is the facade itself, whose <see cref="WorldGrants.TryGrant"/>/<see cref="WorldGrants.Revoke"/>
    /// doors skip the actor check <see cref="Grant"/>/<see cref="Revoke(WorldGrant, Principal, int, long)"/>
    /// run.</remarks>
    public WorldGrants GrantTable => m_grants;
    /// <summary>Gets the per-tick dispatch tally behind the mutation admission budget gate.</summary>
    public WorldMutationBudgetMeter MutationBudget => m_mutationBudget;

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
