using Puck.World.Protocol;
namespace Puck.World.Server;

/// <summary>One destination arrival exactly as it landed: the reservation the source's cohort bound, the body indices
/// the destination assigned, and the commit that embodied the cohort there. The destination's tape carries it so a
/// replay lands the cohort again through the same escrow doors, and the destination's crossing log carries it so a
/// restarted destination lands it again before it answers anyone.</summary>
/// <param name="Request">The reservation request the lease was bound by.</param>
/// <param name="Slots">The destination body indices the reservation assigned, in member order.</param>
/// <param name="Members">The committed travelers, in member order.</param>
/// <param name="Readopted">The companions this arrival re-adopted at their home: each lands in its held slot and its
/// title comes home one epoch above the custody it left.</param>
/// <param name="Generations">The occupant generation each member took where it landed, in member order; empty before the
/// commit has landed it.</param>
/// <param name="Leased">The request-local ids the lease held when the commit landed only part of them, so the partial
/// verdict outlives the transaction's receipt; empty for an arrival that landed every leased member.</param>
public sealed record WorldCrossingArrival(WorldTransferReservationRequest Request, IReadOnlyList<int> Slots, IReadOnlyList<WorldTransferCommitMember> Members, IReadOnlyList<WorldEntityAddress>? Readopted = null, IReadOnlyList<int>? Generations = null, IReadOnlyList<int>? Leased = null) {
    /// <summary>Gets the companions this arrival re-adopted at their home; empty for one that re-adopts none.</summary>
    public IReadOnlyList<WorldEntityAddress> Readopted { get; init; } = (Readopted ?? []);
    /// <summary>Gets the occupant generation each member took where it landed, in member order.</summary>
    public IReadOnlyList<int> Generations { get; init; } = (Generations ?? []);
    /// <summary>Gets the ids the lease held when the commit landed only part of them; empty when it landed them all.</summary>
    public IReadOnlyList<int> Leased { get; init; } = (Leased ?? []);
    /// <summary>Gets the handoff token this arrival committed under.</summary>
    public WorldTransferKey Key => new(
        SourceAuthority: Request.SourceAuthority,
        TransferId: Request.TransferId
    );
}
/// <summary>What one commit decided about an arrival it landed at least one traveler of: the occupant generation each
/// landed traveler's index took, in member order, whether the commit rolled those landings back, and the projection each
/// traveler that came home was bound to. A commit that stood landed every traveler. A rolled-back one landed the leading
/// travelers and then undid them, because the next member was refused or, with every member landed, because its arrival
/// record could not be made durable. A landing advances its index's generation, which outlives the rollback, so a
/// replay reproduces both. A traveler coming home binds its owned identity once that identity adopts what it carried,
/// which can refuse part of it, so the bound projection is the commit's decision too, and a replay binds it rather than
/// deciding the adoption again.</summary>
/// <param name="Generations">The generation each landed traveler's index took, in member order.</param>
/// <param name="RolledBack">Whether the commit rolled its landings back.</param>
/// <param name="Adopted">For a commit that stood, one entry per traveler in member order: the projection a traveler
/// coming home was bound to after its adoption, or <see langword="null"/> for one that did not come home. Empty for a
/// commit that rolled back, which adopts nothing.</param>
/// <param name="Readopted">The companions the commit re-adopted at their home; empty for one that rolled back.</param>
/// <param name="Bindings">For a commit that stood, one entry per traveler in member order: a companion's recorded
/// binding to the player member it follows, or <see langword="null"/> for a traveler that follows none.</param>
public sealed record WorldArrivalOutcome(IReadOnlyList<int> Generations, bool RolledBack, IReadOnlyList<WorldIdentityProjection?> Adopted, IReadOnlyList<WorldEntityAddress>? Readopted = null, IReadOnlyList<WorldCompanionBinding?>? Bindings = null) {
    /// <summary>Gets the companions the commit re-adopted at their home.</summary>
    public IReadOnlyList<WorldEntityAddress> Readopted { get; init; } = (Readopted ?? []);
    /// <summary>Gets each traveler's recorded binding, in member order; empty when none follows anyone.</summary>
    public IReadOnlyList<WorldCompanionBinding?> Bindings { get; init; } = (Bindings ?? []);
}
/// <summary>One crossing step an authority makes durable before the step becomes visible to its peer. Every authority
/// operation appends at most one record, so recovery finds the state between any two of them. A source records
/// its <see cref="Allocation"/> before it reserves under a transfer key, its <see cref="Departure"/> before it sends the
/// commit and its <see cref="Settlement"/> before it acknowledges; a destination records its <see cref="Lease"/> before
/// it accepts a reservation, its <see cref="Arrival"/> before it answers the commit, and a <see cref="Tombstone"/>
/// before it first answers <see cref="WorldTransferStatus.Missing"/> for a key whose death no earlier record shows.
/// Recovery restores the authority's latest checkpoint and redoes, in order, every record that checkpoint does not
/// reflect, so a crash at any step leaves the traveler arrived or never departed and never both, and leaves every key
/// leased at most once. A destination whose arrival record is <see cref="WorldCrossingDurability.Uncertain"/> answers
/// <see cref="WorldTransferStatus.Uncertain"/> until its recovery decides.</summary>
public abstract record WorldCrossingRecord {
    private WorldCrossingRecord() { }

    /// <summary>A destination landed a cohort.</summary>
    /// <param name="Value">The arrival as it landed.</param>
    public sealed record Arrival(WorldCrossingArrival Value) : WorldCrossingRecord;
    /// <summary>A destination leased a transfer key. Recovery leaves the key dead unless an arrival later in the log
    /// lands it, so no reservation can lease it again.</summary>
    /// <param name="Key">The leased key.</param>
    /// <param name="Floor">The source's settled floor the reservation carried
    /// (<see cref="WorldTransferReservationRequest.SourceFloor"/>).</param>
    public sealed record Lease(WorldTransferKey Key, ulong Floor) : WorldCrossingRecord;
    /// <summary>A destination is about to answer <see cref="WorldTransferStatus.Missing"/> for a key: from here no
    /// reservation leases it and no commit lands under it, and a live lease a recovered checkpoint holds for it dies.
    /// A commit that landed members and rolled them back names their slots, whose generations outlive the rollback, so
    /// recovery advances them as the landings did.</summary>
    /// <param name="Key">The dead key.</param>
    /// <param name="Burned">The body indices the refused commit landed at before it rolled them back; empty for a key
    /// that landed nothing.</param>
    public sealed record Tombstone(WorldTransferKey Key, IReadOnlyList<int>? Burned = null) : WorldCrossingRecord {
        /// <summary>Gets the body indices the refused commit landed at before it rolled them back.</summary>
        public IReadOnlyList<int> Burned { get; init; } = (Burned ?? []);
    }
    /// <summary>A source allocated every transfer id below <paramref name="Next"/>: recovery never mints one of them
    /// again.</summary>
    /// <param name="Next">The first id the source has not allocated.</param>
    public sealed record Allocation(ulong Next) : WorldCrossingRecord;
    /// <summary>A source detached a cohort and is about to send its commit. The transfer is in doubt from here until a
    /// settlement: recovery detaches the cohort again and asks the destination for the outcome.</summary>
    /// <param name="Transfer">The source's complete recovery record for the transfer.</param>
    public sealed record Departure(WorldInDoubtTransferCheckpoint Transfer) : WorldCrossingRecord;
    /// <summary>A source settled a departed transfer, per member: the landed members arrived, the retired ones were
    /// destroyed on home's definitive word, and every other member is restored to the slot it left.</summary>
    /// <param name="TransferId">The source-scoped transfer id the departure was recorded under.</param>
    /// <param name="Landed">The request-local ids of the members the destination landed.</param>
    /// <param name="Retired">The request-local ids of the unlanded members the source retired rather than restored.</param>
    /// <param name="Forwarded">The forwarding routes the settlement left for the cohort's admitted travelers.</param>
    public sealed record Settlement(ulong TransferId, IReadOnlyList<int> Landed, IReadOnlyList<int> Retired, IReadOnlyList<WorldForwardedBodyCheckpoint> Forwarded) : WorldCrossingRecord {
        /// <summary>Gets whether any member arrived.</summary>
        public bool Arrived => (Landed.Count > 0);
    }
    /// <summary>Home authorized the hop of every listed companion under one key; each entry is its own title's
    /// fact. From here the title is pending until the hop settles.</summary>
    /// <param name="Key">The crossing's transfer key.</param>
    /// <param name="From">The custodian the hop leaves.</param>
    /// <param name="To">The destination it lands at.</param>
    /// <param name="Members">Each granted companion with the epoch it leaves at and the epoch it lands at.</param>
    public sealed record Hop(WorldTransferKey Key, string From, string To, IReadOnlyList<WorldCustodyHopMember> Members) : WorldCrossingRecord;
    /// <summary>Home settled one companion's hop from the destination's authenticated word (or its own settlement for a
    /// hop of its own). A settlement that reaches home before its hop cancels that hop when it is recorded.</summary>
    /// <param name="Key">The hop's key.</param>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="Arrived">Whether the destination landed it.</param>
    public sealed record HopDone(WorldTransferKey Key, WorldEntityAddress Incarnation, bool Arrived) : WorldCrossingRecord;
    /// <summary>Home acknowledged this custodian's settlement notice for one companion's hop.</summary>
    /// <param name="Key">The hop's key.</param>
    /// <param name="Incarnation">The companion.</param>
    public sealed record HopAcked(WorldTransferKey Key, WorldEntityAddress Incarnation) : WorldCrossingRecord;
    /// <summary>Home recalled a companion that was away: its last committed image is embodied in the held slot at
    /// <paramref name="Epoch"/>, above every epoch home issued, and every pending hop of it is superseded.</summary>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="Epoch">The recall's epoch.</param>
    public sealed record Recall(WorldEntityAddress Incarnation, ulong Epoch) : WorldCrossingRecord;
    /// <summary>Home told the player of a recall or a release, discharging that notice.</summary>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="What">What the notice told.</param>
    /// <param name="Epoch">The recall's epoch, or zero for a release.</param>
    public sealed record Noticed(WorldEntityAddress Incarnation, WorldCustodyNotice What, ulong Epoch) : WorldCrossingRecord;
    /// <summary>A definition install removed the placement's role: the title is released (once its pending hop, if
    /// any, resolves).</summary>
    /// <param name="Incarnation">The companion.</param>
    public sealed record Release(WorldEntityAddress Incarnation) : WorldCrossingRecord;
    /// <summary>A custodian destroyed its embodied copy on home's definitive answer.</summary>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="Cause">What home answered.</param>
    public sealed record Retire(WorldEntityAddress Incarnation, WorldCustodyRetireCause Cause) : WorldCrossingRecord;
    /// <summary>A custodian found a companion separated with no way home during the player's
    /// <paramref name="Visit"/>th visit here: its grace window runs to <paramref name="DeadlineTick"/>.</summary>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="Visit">The visit the stranding belongs to.</param>
    /// <param name="DeadlineTick">The custodian tick the grace window ends at.</param>
    public sealed record Stranded(WorldEntityAddress Incarnation, int Visit, ulong DeadlineTick) : WorldCrossingRecord;
    /// <summary>A stranded companion's grace window passed.</summary>
    /// <param name="Incarnation">The companion.</param>
    /// <param name="Visit">The visit the stranding belongs to.</param>
    public sealed record GraceElapsed(WorldEntityAddress Incarnation, int Visit) : WorldCrossingRecord;
}
/// <summary>One record in an authority's crossing log, in the order the authority made it durable.</summary>
/// <param name="Sequence">The authority's crossing sequence: dense from zero, never reused, and captured by every
/// checkpoint as the first sequence that checkpoint does not reflect.</param>
/// <param name="Tick">The authority tick the record was made at.</param>
/// <param name="Record">The record.</param>
public readonly record struct WorldCrossingEntry(ulong Sequence, ulong Tick, WorldCrossingRecord Record);
/// <summary>What a crossing log can say about one append.</summary>
public enum WorldCrossingDurability : byte {
    /// <summary>The record is durable; recovery redoes it.</summary>
    Durable = 0,

    /// <summary>The record did not land and never will; recovery cannot see it.</summary>
    Refused = 1,

    /// <summary>The record may or may not be durable, and only a later activation's recovery from the log can tell.
    /// The authority records nothing more until it recovers: a later record would take the same sequence.</summary>
    Uncertain = 2,
}
/// <summary>The durable log an authority writes its crossing records ahead through. An implementation answers only
/// once it knows what became of the record, and refuses an append from an activation its store has fenced off, so a
/// superseded authority can never claim a step its successor will not recover. An authority with no log installed
/// records nothing and keeps no crossing beyond its own process.</summary>
public interface IWorldCrossingLog {
    /// <summary>Appends one record and reports what became of it.</summary>
    /// <param name="entry">The record and its sequence.</param>
    /// <param name="reason">Why the record is not known to be durable, when it is not; empty otherwise.</param>
    /// <returns><see cref="WorldCrossingDurability.Durable"/> once the record is durable,
    /// <see cref="WorldCrossingDurability.Refused"/> when it certainly did not land, or
    /// <see cref="WorldCrossingDurability.Uncertain"/> when the store's answer was lost.</returns>
    WorldCrossingDurability Append(in WorldCrossingEntry entry, out string reason);
}
