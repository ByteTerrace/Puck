using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The surface of <see cref="WorldGrants"/> reachable through <see cref="WorldServer.Grants"/> — every read the
/// engagement view, the addon runtime, and the grant/mutation command modules need, plus the two control-application
/// writes (<see cref="SetApplications"/>/<see cref="ClearApplications"/>) that record a set the caller already
/// permission-checked, rather than administer authority. <see cref="WorldGrants.TryGrant"/> and
/// <see cref="WorldGrants.Revoke"/> — the two doors that add or remove authority — are deliberately absent from this
/// interface: those run only behind <see cref="WorldServer.Grant"/>/<see cref="WorldServer.Revoke"/>'s
/// <see cref="WorldGrants.HoldsForAdministration"/> actor check, and a caller holding only this interface can never
/// reach them, because the concrete <see cref="WorldGrants"/> instance stays private to <see cref="WorldServer"/>.
/// </summary>
public interface IWorldGrantsView {
    /// <summary>Determines whether <paramref name="principal"/> holds <paramref name="capability"/> over <paramref name="subject"/>
    /// — the allocation-free, O(1) hot-path check, returning which rule decided rather than a bare
    /// <see langword="bool"/> (a decision is data, never a boolean; the <see cref="GrantVerdict"/> converts
    /// implicitly so boolean call sites read unchanged). When the subject is exclusively reserved, only the
    /// reserver is allowed (the exclusivity override — the exclusive holder beats every other grant, including the
    /// wildcard), and a beaten caller's verdict names the reserver: being beaten is a different state than never
    /// having been granted, and the caller could not know which without this — the bool collapsed them before any
    /// message site ever saw them. Otherwise the principal's subject set decides: the concrete row is reported in
    /// preference to the wildcard when both hold (the more specific basis).</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to test.</param>
    /// <param name="subject">The subject to test.</param>
    GrantVerdict Allows(WorldPrincipal principal, WorldCapability capability, GrantSubject subject);
    /// <summary>Determines whether <paramref name="principal"/> holds <paramref name="capability"/> over every
    /// <see cref="WorldSection"/> — the check a whole-document swap or journal undo passes (it can touch any section).
    /// A failure names the first refusing section and its <see cref="GrantVerdict"/>, so "cannot mutate every section"
    /// stops being a message the operator has to bisect with twenty-five <c>world.grants</c> reads — the denial says
    /// which section refused and which rule refused it.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to test (today <see cref="WorldCapability.Mutate"/>).</param>
    /// <param name="deniedSection">The first section that refused, when the check fails.</param>
    /// <param name="denial">The refusing section's verdict, when the check fails.</param>
    bool AllowsAllSections(WorldPrincipal principal, WorldCapability capability, out WorldSection deniedSection, out GrantVerdict denial);
    /// <summary>Renders the grant table for the <c>world.grants</c> echo — one bracketed segment per principal, or one
    /// principal's rows when <paramref name="filter"/> is set. Diagnostics only; not on any tick path.</summary>
    /// <param name="filter">A single principal to describe, or <see langword="null"/> for the whole table.</param>
    /// <returns>The echo string.</returns>
    string Describe(WorldPrincipal? filter);
    /// <summary>Returns the <paramref name="principal"/>/<paramref name="capability"/> handle table — built on first request
    /// and cached after (a <see cref="WorldHandleTable"/> re-projects on its own when <see cref="Revision"/>
    /// moves, so the cached instance never needs replacing, only asking again). Refuses any principal but
    /// <see cref="PrincipalKind.Addon"/>/<see cref="PrincipalKind.Peer"/> — see <see cref="WorldHandleTable"/>'s own
    /// remarks for why Console and Seat, which could grant themselves anything, never get one.</summary>
    /// <param name="principal">The principal outside the trust boundary the table is for.</param>
    /// <param name="capability">The capability the table designates handles over.</param>
    WorldHandleTable HandleTable(WorldPrincipal principal, WorldCapability capability);
    /// <summary>Returns the per-tick dispatch budget <paramref name="principal"/>'s row for <paramref name="capability"/> over
    /// <paramref name="subject"/> carries, or <see langword="false"/> when the row carries none — either it is
    /// unmetered (a trusted principal, or a capability other than Observe/Drive, both of which
    /// <see cref="WorldGrants.TryGrant"/> refuses a budget on) or the row does not exist at all; this makes no
    /// membership claim of its own, so a caller that needs to know whether the row is held asks <see cref="Allows"/>
    /// first. Read fresh per query, exactly like <see cref="Allows"/> and for the same staleness reason: a re-grant
    /// with a different budget (last-write-wins) must take effect on its very next dispatch, never lag behind a
    /// cached decision.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to query.</param>
    /// <param name="subject">The subject to query.</param>
    /// <param name="budget">The row's budget, when it carries one.</param>
    bool TryGetBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget);
    /// <summary>Returns the per-tick event-cell budget <paramref name="principal"/>'s Observe row over <paramref name="subject"/>
    /// carries, or <see langword="false"/> when the row carries none (either it holds no event budget, or it does
    /// not hold the row at all). A sibling to <see cref="TryGetBudget"/> over the identical key, metering a
    /// different cost: event push volume rather than query dispatch (see <c>WorldGrant.EventBudget</c>'s own doc).
    /// Read fresh per query, exactly like <see cref="TryGetBudget"/> and for the same staleness reason.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to query (today only <see cref="WorldCapability.Observe"/> ever carries one).</param>
    /// <param name="subject">The subject to query.</param>
    /// <param name="budget">The row's event budget, when it carries one.</param>
    bool TryGetEventBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget);
    /// <summary>Returns the effective timed-press ceiling in raw Q48.16 seconds from the Drive row that decides authority.
    /// A concrete row wins over a wildcard row; an omitted payload yields <see cref="WorldGrant.DefaultHoldSeconds"/>.</summary>
    /// <param name="principal">The driving principal.</param>
    /// <param name="subject">The body subject.</param>
    long HoldCeiling(WorldPrincipal principal, GrantSubject subject);
    /// <summary>Returns the channel reach <paramref name="principal"/>'s Drive row over <paramref name="subject"/> carries —
    /// which ordinals this contributor may touch at all — or <see langword="false"/> when the row carries none. A miss
    /// is default-deny: the fold's caller treats it as an empty reach, never as an inferred one. Reach is not consent;
    /// a reached channel still folds nothing until the occupying seat authored a ceiling for it
    /// (<see cref="PoolCeilings"/>). Read fresh per query, exactly like <see cref="TryGetBudget"/> and for the same
    /// staleness reason: a re-grant with a different mask (last-write-wins) takes effect on its very next fold.</summary>
    /// <param name="principal">The contributing principal (typically an addon).</param>
    /// <param name="subject">The body subject.</param>
    /// <param name="mask">The reached channel bitmask, when the row carries one.</param>
    bool TryGetChannelReach(WorldPrincipal principal, GrantSubject subject, out ChannelReachMask mask);
    /// <summary>Returns the occupying seat's own per-channel pool ceilings over its body — one number per (seat, channel),
    /// authored by the seat and never derived from any contributor row. Empty when the seat has authored none, which
    /// is default-deny for every ordinal: an untrusted contribution to a channel with no authored ceiling folds
    /// nothing. Its support mask is carried by the same value and is set exactly where a ceiling is positive.</summary>
    /// <param name="seat">The occupying seat principal.</param>
    /// <param name="subject">The seat's own body subject.</param>
    ChannelCeilings PoolCeilings(WorldPrincipal seat, GrantSubject subject);
    /// <summary>Returns the <see cref="MutationKindMask"/> <paramref name="principal"/>'s row for <paramref name="capability"/>
    /// over <paramref name="subject"/> carries, or <see langword="false"/> when the row carries none — the
    /// mutation-kind narrowing beneath a Mutate/<c>section:</c> or Edit/<c>state:</c> hold (see
    /// <see cref="WorldGrant.KindMask"/>). No mask means full reach over the subject the hold already cleared, never
    /// deny: the mask is opt-in narrowing, and deny-by-default is the hold's own job. Read fresh per query, exactly
    /// like <see cref="TryGetBudget"/> and for the same staleness reason.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to query (<see cref="WorldCapability.Mutate"/> or <see cref="WorldCapability.Edit"/>).</param>
    /// <param name="subject">The subject to query.</param>
    /// <param name="mask">The row's mask, when it carries one.</param>
    /// <returns><see langword="true"/> when the row carries a kind mask.</returns>
    bool TryGetKindMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out MutationKindMask mask);
    /// <summary>Returns the <see cref="DocumentWriteMask"/> <paramref name="principal"/>'s row carries — the cross-document
    /// durable-state channel's own Set/Add narrowing (see <see cref="WorldGrant.WriteMask"/>), a different vocabulary
    /// from <see cref="TryGetKindMask"/> and therefore a different accessor: the two masks share a bit-lane shape and
    /// nothing else.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to query (today only <see cref="WorldCapability.Mutate"/> carries one).</param>
    /// <param name="subject">The subject to query.</param>
    /// <param name="mask">The row's mask, when it carries one.</param>
    /// <returns><see langword="true"/> when the row carries a write mask.</returns>
    bool TryGetWriteMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out DocumentWriteMask mask);
    /// <summary>Returns the principal exclusively reserving <paramref name="subject"/> for <paramref name="capability"/>, or
    /// <see langword="null"/> when it is unreserved — the editor HUD's exclusive-hold readout. Same wildcard-aware
    /// lookup <see cref="Allows"/> enforces with; allocation-free, human-cadence reads only.</summary>
    /// <param name="capability">The capability to query.</param>
    /// <param name="subject">The subject to query.</param>
    WorldPrincipal? ExclusiveHolder(WorldCapability capability, GrantSubject subject);
    /// <summary>Returns every (capability, subject) pair <paramref name="principal"/> currently holds, across all five
    /// capabilities, in a deterministic order — capability declaration order, then a stable (kind, value, id) order,
    /// never <see cref="HashSet{T}"/> enumeration order (a free-list/insertion-history artifact). This is the one
    /// disclosure primitive: <see cref="Describe"/> (the <c>world.grants</c> echo) and any mount-time or audit report
    /// of what a principal actually holds read through this, never the raw per-capability sets directly, so none of
    /// them can independently drift from what the table contains or report merely the intersection of what was asked
    /// for and what was held.</summary>
    /// <param name="principal">The principal to project.</param>
    IReadOnlyList<(WorldCapability Capability, GrantSubject Subject)> Held(WorldPrincipal principal);
    /// <summary>Returns the <see cref="ControlApplication"/> set <paramref name="principal"/> holds — the whole of
    /// its engagement state. A participant that has composed nothing reports the default single own-body
    /// application, so an unengaged seat and an engaged one are read the same way; a principal with no body of its
    /// own (Console, Addon, World) reports an empty set. The returned list is the live storage, invalidated by the
    /// next <see cref="SetApplications"/>/<see cref="ClearApplications"/> on the same principal.</summary>
    /// <param name="principal">The principal.</param>
    IReadOnlyList<ControlApplication> Applications(WorldPrincipal principal);
    /// <summary>Collects every principal whose COMPOSED application set applies to <paramref name="target"/> into
    /// <paramref name="into"/> (cleared first) — the multiplayer-cabinet merge set for a screen target. An
    /// uncomposed participant's implicit own-body application is not a composition and is never collected.</summary>
    /// <param name="target">The application target subject (screen or body).</param>
    /// <param name="into">The reusable destination list.</param>
    void CollectApplicationHolders(GrantSubject target, List<WorldPrincipal> into);
    /// <summary>Replaces <paramref name="principal"/>'s application set. The permission to apply (a Control grant
    /// over each target, or the wildcard) is checked separately by the caller; this only records the resolved set.
    /// A set equal to the default is stored as the default itself, never as a composition.</summary>
    /// <param name="principal">The principal.</param>
    /// <param name="applications">The complete replacement set.</param>
    void SetApplications(WorldPrincipal principal, IReadOnlyList<ControlApplication> applications);
    /// <summary>Restores <paramref name="principal"/>'s set to its own-body default. Returns whether it had composed
    /// anything.</summary>
    /// <param name="principal">The principal.</param>
    bool ClearApplications(WorldPrincipal principal);

    /// <summary>Gets the grant-table change counter a <see cref="WorldHandleTable"/> (and the addon runtime's own
    /// disclosure cache) compares against its own last-seen value to decide whether it must re-project before
    /// answering. Bumped by every mutator that changes a principal's held subject sets. Monotonic for the life of the
    /// table; wrapping is not a concern at human grant/revoke cadence.</summary>
    int Revision { get; }

    /// <summary>Projects every concrete subject <paramref name="principal"/> holds <paramref name="capability"/> over
    /// into a deterministically ordered array — the read half of a <see cref="WorldHandleTable"/> rebuild, and the
    /// addon runtime's own disclosure-set projection. Allocates one array per call; a rebuild is grant/revoke-cadence,
    /// never the tick path.</summary>
    /// <param name="principal">The principal to project.</param>
    /// <param name="capability">The capability to project.</param>
    GrantSubject[] ProjectSubjects(WorldPrincipal principal, WorldCapability capability);
    /// <summary>Determines whether <paramref name="bodyIndex"/> is currently drive-gated (composition-core's CC/death gating,
    /// Seam A) — carries a nonzero cell on a state row declaring <see cref="WorldStateRow.GatesDrive"/> — and, when
    /// it is, which row decided it. Checked fresh against the index <see cref="WorldGrants.SyncState"/> last
    /// resynced; never latched. <c>world.why</c>'s own read-back is the intended caller through this interface —
    /// the enforcement site (<see cref="WorldServer.ApplyIntentSubmission"/>) sits on the concrete
    /// <see cref="WorldGrants"/> and does not need this seam.</summary>
    /// <param name="bodyIndex">The 0-based entity index to check.</param>
    /// <param name="gateRow">The deciding row's name, when gated; empty otherwise.</param>
    /// <returns><see langword="true"/> when the body is gated.</returns>
    bool TryGetDriveGate(int bodyIndex, out string gateRow);
}
