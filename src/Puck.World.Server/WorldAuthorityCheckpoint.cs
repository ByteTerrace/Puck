using System.Numerics;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One buffered live-edit op awaiting the next <c>Step</c>'s drain — the checkpointed mirror of
/// <c>WorldServer</c>'s own private <c>PendingOp</c> union, carrying the identical payload under a public shape a
/// checkpoint's record graph can hold.</summary>
public abstract record WorldPendingOpCheckpoint {
    /// <summary>A buffered document mutation.</summary>
    public sealed record Mutate(WorldMutation Mutation, int ConnectionId, long CorrelationId, long SourceAddonInstanceId, ushort ActOrdinal) : WorldPendingOpCheckpoint;
    /// <summary>A buffered whole-document rebuild-and-swap.</summary>
    public sealed record Rebuild(WorldRebuildRequest Request, WorldPrincipal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash, string? PreparationFailure) : WorldPendingOpCheckpoint;
    /// <summary>A buffered journal undo.</summary>
    public sealed record Undo(int Count, WorldPrincipal Principal, int ConnectionId, long CorrelationId) : WorldPendingOpCheckpoint;
}
/// <summary>One landed member's checkpointed state — the subset of the host engine's own private
/// <c>WorldInstanceHost.LandedMember</c> that a co-hosted <c>Committed</c> resolution
/// (<c>WorldInstanceHost.FinalizeCommittedTransfer</c>) and an <c>Aborted</c>/<c>Missing</c> rollback
/// (<c>WorldInstanceHost.RestoreDetachedMember</c>) each read on restore. Two of the live type's fields are
/// deliberately absent: <c>Profile</c> is re-derived from the corresponding entry of
/// <see cref="WorldInDoubtTransferCheckpoint.CommitMembers"/> at the SAME ordinal (both lists are built from the one
/// <c>profile</c> local in the same detach loop, so the two never disagree), and <c>SourcePrincipal</c> is stamped at
/// construction but read by neither resolution path — a value nothing downstream reads earns no place in the
/// checkpoint.</summary>
/// <param name="SourceSlot">The source row's own local seat this member detached from.</param>
/// <param name="TargetSlot">The destination's reserved body index this member is bound for.</param>
/// <param name="BodyColor">The source body's exact rendered material color, preserved across a rollback.</param>
/// <param name="Position">The member's exact SOURCE pose position at detach — the rollback anchor, distinct from
/// <see cref="WorldTransferCommitMember.Position"/>'s (possibly frame-mapped) ARRIVAL pose.</param>
/// <param name="Yaw">The member's exact SOURCE yaw at detach — see <paramref name="Position"/>.</param>
/// <param name="DynamicState">The member's captured <c>WorldBody.TransferState</c> at detach (velocity, dash
/// overlay, in-flight timed presses) — the rollback restores this verbatim.</param>
/// <param name="Designations">The member's captured designation rows at detach.</param>
/// <param name="Peer">The member's peer admission record when it detached from the PEER range, or
/// <see langword="null"/> for a local seat — read by a co-hosted commit resolution to decide whether an onward
/// forwarded arm is owed.</param>
/// <param name="AdmissionGrants">The peer's own admission grant templates, re-installed on a rollback.</param>
/// <param name="SourceGrants">The exact source-held grant rows this member carried, re-administered on a
/// rollback.</param>
/// <param name="Mobility">The member's stable mobility identity, at the epoch it held when it detached — advanced
/// once more by a co-hosted commit resolution when it mints the onward forwarded arm's lease.</param>
public sealed record WorldLandedMemberCheckpoint(
    int SourceSlot,
    int TargetSlot,
    Vector3 BodyColor,
    FixedVector3 Position,
    FixedQ4816 Yaw,
    WorldBody.TransferState DynamicState,
    IReadOnlyList<WorldTargetDesignation> Designations,
    WorldPeerEventEntry? Peer,
    IReadOnlyList<WorldAdmissionGrant> AdmissionGrants,
    IReadOnlyList<WorldGrant> SourceGrants,
    WorldMobilityIdentity Mobility
);
/// <summary>One in-doubt transfer's checkpointed state, addressed as data — never the live
/// <see cref="WorldInstanceHost"/> peer handle a restore re-materializes on demand.</summary>
/// <param name="SourceInstance">The source row's own registry name.</param>
/// <param name="TransferId">The transfer id, scoped to <paramref name="SourceInstance"/>.</param>
/// <param name="TargetName">The registry name of a co-hosted target row, or <see langword="null"/> for a remote
/// target addressed by <paramref name="TargetAuthority"/> alone.</param>
/// <param name="TargetAuthority">The target row's own federation subject — always present, so a remote target is
/// addressable even when <paramref name="TargetName"/> is unknown to this row.</param>
/// <param name="TargetEndpoint">The target row's own federation endpoint (host:port) when the target was REMOTE at
/// capture time, or <see langword="null"/> for a co-hosted target — not re-derivable (a remote peer call's endpoint
/// is live connection state), so it is captured even though this lane's restore does not yet dial it (see
/// <see cref="WorldInstanceHost.RestoreRow"/>'s own remarks on the remaining gap).</param>
/// <param name="Spawned">Whether this crossing minted a fresh destination instance.</param>
/// <param name="SourceDeadlineTick">The source authority tick past which this in-doubt transfer's retry ceiling is
/// reached.</param>
/// <param name="MemberCount">How many members this transfer carries.</param>
/// <param name="CommitMembers">The exact commit payload a retried <c>Commit</c> call must resubmit — the data
/// <c>WorldInstanceHost.ReconcileInDoubtTransfers</c> needs to retry a still-<c>Reserved</c> destination correctly;
/// without it a restore could only ask <c>TryStatus</c>, never drive a reservation to resolution itself.</param>
/// <param name="Landed">Every member's own rollback state — see <see cref="WorldLandedMemberCheckpoint"/>.</param>
public sealed record WorldInDoubtTransferCheckpoint(
    string SourceInstance,
    ulong TransferId,
    string? TargetName,
    string TargetAuthority,
    string? TargetEndpoint,
    bool Spawned,
    ulong SourceDeadlineTick,
    int MemberCount,
    IReadOnlyList<WorldTransferCommitMember> CommitMembers,
    IReadOnlyList<WorldLandedMemberCheckpoint> Landed
);
/// <summary>One forwarded body's checkpointed state, addressed as data — the destination identity and mobility
/// credential a restore re-materializes a fresh <see cref="IWorldForwardedAuthority"/> arm from, never the live
/// arm itself (which holds a lease id or a lane no checkpoint may carry).</summary>
public sealed record WorldForwardedBodyCheckpoint(
    WorldEntityAddress SourceIncarnation,
    WorldEntityAddress DestinationAddress,
    int DestinationBodyIndex,
    WorldMobilityIdentity Mobility
);
/// <summary>One row's slice of the host engine's cross-instance tables — everything <see cref="WorldInstanceHost"/>
/// owns on this row's behalf that a later tick reads and nothing on the restore path re-derives.</summary>
public sealed record WorldAuthorityHostRowCheckpoint(
    ulong ScheduleAccumulatorTicks,
    ulong ElapsedEngineTicks,
    bool IsPaused,
    IReadOnlyList<(string PlacementId, string FaceName, int Seat)> PortalOccupancy,
    ulong NextTransferId,
    IReadOnlyList<WorldInDoubtTransferCheckpoint> InDoubtTransfers,
    IReadOnlyList<WorldForwardedBodyCheckpoint> ForwardedBodies,
    IReadOnlyList<ulong> AppliedTransferIds,
    ulong? AppliedTransferHighWater,
    int FreshCounter,
    bool Retained,
    IReadOnlyList<(int Seat, ulong TransferId)> AnnouncedCrossingHolds,
    IReadOnlyList<(int Seat, string Border)> SeededArrivals
);
/// <summary>
/// A full simulation-state image of one <see cref="WorldServer"/> and the subsystems it owns, plus this row's slice
/// of the host engine — see <see cref="WorldServer.TryCaptureCheckpoint"/> for the capture point, the arm gate, and
/// what each section excludes and why.
/// </summary>
public sealed record WorldAuthorityCheckpoint(
    WorldServer.WorldServerCheckpoint Server,
    WorldPopulation.WorldPopulationCheckpoint Population,
    WorldGrants.WorldGrantsCheckpoint Grants,
    WorldTransferEscrow.WorldTransferEscrowCheckpoint Escrow,
    WorldInputHoldRuntime.WorldInputHoldCheckpoint InputHold,
    WorldEventFeed.WorldEventFeedCheckpoint EventFeed,
    WorldOwnedWorlds.WorldOwnedWorldsCheckpoint OwnedWorlds,
    WorldAuthorityHostRowCheckpoint HostRow
);
