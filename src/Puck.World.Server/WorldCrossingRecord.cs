namespace Puck.World.Server;

/// <summary>One destination arrival exactly as it landed: the reservation the source's cohort bound, the body indices
/// the destination assigned, and the commit that embodied the cohort there. The destination's tape carries it so a
/// replay lands the cohort again through the same escrow doors, and the destination's crossing log carries it so a
/// restarted destination lands it again before it answers anyone.</summary>
/// <param name="Request">The reservation request the lease was bound by.</param>
/// <param name="Slots">The destination body indices the reservation assigned, in member order.</param>
/// <param name="Members">The committed travelers, in member order.</param>
public sealed record WorldCrossingArrival(WorldTransferReservationRequest Request, IReadOnlyList<int> Slots, IReadOnlyList<WorldTransferCommitMember> Members) {
    /// <summary>Gets the handoff token this arrival committed under.</summary>
    public WorldTransferKey Key => new(
        SourceAuthority: Request.SourceAuthority,
        TransferId: Request.TransferId
    );
}
/// <summary>One crossing step an authority makes durable before the step becomes visible to its peer. A source records
/// its <see cref="Departure"/> before it sends the commit and its <see cref="Settlement"/> before it acknowledges; a
/// destination records its <see cref="Arrival"/> before it answers the commit. Recovery restores the authority's
/// latest checkpoint and redoes, in order, every record that checkpoint does not reflect, so a crash at any step
/// leaves the traveler arrived or never departed and never both.</summary>
public abstract record WorldCrossingRecord {
    private WorldCrossingRecord() { }

    /// <summary>A destination landed a cohort.</summary>
    /// <param name="Value">The arrival as it landed.</param>
    public sealed record Arrival(WorldCrossingArrival Value) : WorldCrossingRecord;
    /// <summary>A source detached a cohort and is about to send its commit. The transfer is in doubt from here until a
    /// settlement: recovery detaches the cohort again and asks the destination for the outcome.</summary>
    /// <param name="Transfer">The source's complete recovery record for the transfer.</param>
    public sealed record Departure(WorldInDoubtTransferCheckpoint Transfer) : WorldCrossingRecord;
    /// <summary>A source settled a departed transfer: published the destination's commit, or restored every member.</summary>
    /// <param name="TransferId">The source-scoped transfer id the departure was recorded under.</param>
    /// <param name="Arrived">Whether the cohort arrived; <see langword="false"/> means every member was restored.</param>
    /// <param name="Forwarded">The forwarding routes the settlement left for the cohort's admitted travelers.</param>
    public sealed record Settlement(ulong TransferId, bool Arrived, IReadOnlyList<WorldForwardedBodyCheckpoint> Forwarded) : WorldCrossingRecord;
}
/// <summary>One record in an authority's crossing log, in the order the authority made it durable.</summary>
/// <param name="Sequence">The authority's crossing sequence: dense from zero, never reused, and captured by every
/// checkpoint as the first sequence that checkpoint does not reflect.</param>
/// <param name="Tick">The authority tick the record was made at.</param>
/// <param name="Record">The record.</param>
public readonly record struct WorldCrossingEntry(ulong Sequence, ulong Tick, WorldCrossingRecord Record);
/// <summary>The durable log an authority writes its crossing records ahead through. An implementation answers only
/// after the record is durable, and refuses an append from an activation its store has fenced off, so a superseded
/// authority can never claim a step its successor will not recover. An authority with no log installed records
/// nothing and keeps no crossing beyond its own process.</summary>
public interface IWorldCrossingLog {
    /// <summary>Makes one record durable.</summary>
    /// <param name="entry">The record and its sequence.</param>
    /// <param name="reason">Why the record did not land, when it did not.</param>
    /// <returns><see langword="true"/> once the record is durable; <see langword="false"/> refuses the step the
    /// record was written ahead of.</returns>
    bool TryAppend(in WorldCrossingEntry entry, out string reason);
}
