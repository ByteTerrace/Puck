using System.Globalization;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The server's ONE capability table — the single primitive that engagement, machine-input ownership, and addon
/// slot ownership all reduce to: a set of <c>(principal, capability, subject)</c>
/// grants, seeded permissive for local play and mutated live through <c>world.grant</c>/<c>world.revoke</c>. Every
/// write boundary asks <see cref="Allows"/> before it acts; <c>Puck.World.WorldEngagement</c> is a VIEW over the
/// <see cref="WorldCapability.Control"/> screen routes here, not a parallel table.
/// </summary>
/// <remarks>
/// <para>Storage is a per-principal record of five subject sets (one per capability), so a per-tick
/// <see cref="Allows"/> is a dictionary lookup plus one <see cref="HashSet{T}"/> membership test — allocation-free and
/// O(1). A grant matches when its subject set holds the queried subject OR the <see cref="GrantSubject.All"/> wildcard.</para>
/// <para><b>Exclusivity — the chosen semantic (an exclusive hold is enforced, not just reserved).</b> Exclusivity is a
/// reservation over ONE concrete <c>(capability, subject)</c>, tracked in a reverse index. An exclusive grant MUST name
/// a concrete subject: an exclusive <see cref="GrantSubject.All"/> ("exclusively own everything") is REJECTED at
/// acquisition in every order and on a fresh table — it has no legitimate consumer and would otherwise be
/// order-dependently dishonest (accepted one way, then denying every concrete seat at enforcement; rejected the other).
/// A concrete exclusive hold is honored in two places that together give the invariant "an exclusively-held body has
/// exactly ONE effective driver":</para>
/// <list type="bullet">
/// <item><description><b>Acquisition (<see cref="TryGrant"/>).</b> An incoming grant — exclusive OR ordinary — is
/// rejected when it would put a DIFFERENT principal alongside an existing conflicting hold: (1) any exclusive
/// reservation of an overlapping subject blocks it in EITHER order (exclusive-then-ordinary and ordinary-then-exclusive
/// both reject), and (2) an incoming exclusive additionally rejects when a different principal already holds the SAME
/// concrete subject ordinarily. An incoming exclusive <see cref="GrantSubject.All"/> is rejected outright (above). The
/// wildcard <see cref="GrantSubject.All"/> ORDINARY grant is DELIBERATELY EXEMPT from rule (1) in BOTH DIRECTIONS —
/// an existing exclusive concrete hold never blocks a LATER ordinary wildcard re-grant, and the seeded wildcard never
/// blocks a LATER exclusive concrete acquisition: the permissive local defaults seed the console with <c>Drive/all</c>
/// and seats with <c>Control/all</c>; an admitted peer generation receives that row from its
/// <c>PeerAdmitted</c> server event
/// (NOT <c>Drive</c>) — so by default nothing drives an inhabited body (Arc 7) except its own producer, and possessing
/// one is an explicit <c>world.grant &lt;principal&gt; drive body:&lt;index&gt;</c>. That is the correct default and it
/// comes for free from the existing seed; no new grant subject or capability is added for inhabitation.
/// This backdrop must never block a principal (e.g. an addon) from taking an exclusive hold on one specific body —
/// so <c>world.grant addon:x drive body:n exclusive</c> succeeds even though the console holds <c>Drive/all</c> — and
/// the exclusive hold must never PERMANENTLY prevent the wildcard's later re-grant either: narrowing
/// (<c>world.revoke console drive all</c>) then re-widening (<c>world.grant console drive all</c>) succeeds no
/// matter what exclusive concrete holds exist elsewhere, because enforcement (<see cref="Allows"/>) — not
/// acquisition-time blocking — is what keeps an exclusive holder the sole effective owner of ITS OWN subject. The
/// SEEDED per-section <c>Mutate</c> defaults get the same exemption: they are the concrete spelling of the same
/// permissive backdrop (per-section only so one is revocable), so
/// <c>world.grant seat1 mutate section:screens exclusive</c> succeeds on a default table — the seed must never block
/// an exclusive editing hold. A section row DELIBERATELY granted after boot (or re-granted after a revoke) is a real
/// hold and blocks like any other; only the untouched seed is exempt.</description></item>
/// <item><description><b>Enforcement (<see cref="Allows"/>).</b> Once <c>body:n</c> is exclusively reserved by principal
/// P, <see cref="Allows"/> answers TRUE only for P — the exclusive holder OVERRIDES every other grant, INCLUDING the
/// permissive <c>Drive/all</c> wildcard. So the exempt backdrop from acquisition cannot actually drive an exclusively
/// held body: exclusivity, not acquisition-time blocking, is what makes the reservation exclusive at the intent
/// boundary. When a subject is not exclusively reserved, the normal wildcard/subject-set logic applies unchanged.</description></item>
/// </list>
/// <para>Single-threaded, like every server type here: grants apply in the command-apply window and are read at the
/// tick boundary, both on the launcher's window-pump thread. No lock guards this state.</para>
/// </remarks>
public sealed class WorldGrants : IWorldGrantsView {
    private static readonly long s_defaultHoldCeiling = Puck.Maths.FixedQ4816.FromDouble(value: WorldGrant.DefaultHoldSeconds).Value;
    // The entity-table ceiling passed at construction — the same value WorldServer.Body(int) bounds against — so a
    // Drive grant can never legitimately name a body index the population does not actually hold (see
    // IsLegitimateSubject).
    private readonly int m_population;
    private readonly Action<WorldPrincipal, GrantSubject?, GrantSubject?> m_routeTransition;
    private readonly Dictionary<WorldPrincipal, PrincipalGrants> m_byPrincipal = new();
    // (capability, subject) -> the exclusive holder. Guards double-exclusive acquisition (the engagement latch's
    // "a live holder owns it" rule, generalized). Only exclusive grants appear here.
    private readonly Dictionary<ExclusiveKey, WorldPrincipal> m_exclusive = new();
    // (principal, capability, subject) -> the row's per-tick dispatch budget — the exclusivity precedent applied
    // verbatim to a SECOND non-key payload lane: a parallel keyed structure beside the five bare per-capability sets,
    // never a widening of them (see the type doc's remarks on why Exclusive got its own dictionary rather than a
    // richer set element). Written UNCONDITIONALLY by TryGrant on every accepted grant that carries a budget
    // (last-write-wins — a re-grant IS the budget-update verb, no new grammar needed), cleared by Revoke (the fourth
    // cleanup site, beside m_exclusive/m_seededSections/the handle-table cache's revision bump). Only an untrusted
    // principal's (Addon/Peer) metered row ever has an entry here — Observe, Drive, or Mutate over a concrete
    // section:<name> — see TryGrant's budget checks.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ushort> m_budgets = new();
    // The EVENT-CELL budget — a SIBLING to m_budgets over the identical (principal, capability, subject) key, never a
    // widening of it: Budget meters QUERY DISPATCH (a guest asking), this meters EVENT PUSH volume (the host telling)
    // — two different costs a single Observe row may carry independently (see WorldGrant.EventBudget's own doc).
    // Written unconditionally by TryGrant, cleared by Revoke, exactly like m_budgets.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ushort> m_eventBudgets = new();
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), long> m_holdCeilings = new();
    // The co-driving payload, split into the TWO things the design actually names — the same parallel-keyed-structure
    // precedent as m_budgets above, applied twice:
    //
    // m_channelReach: an UNTRUSTED contributor's Drive row -> which channel ordinals it may touch at all. Reach, not
    // consent: a reach mask contributes nothing until the occupying seat authors a ceiling for that channel.
    //
    // m_poolCeilings: the OCCUPYING SEAT'S OWN Drive row (seatN drive body:N) -> ONE ceiling per channel ordinal, the
    // number that bounds how far the pool may pull that channel. Stored with its positive-value support mask as one
    // ChannelCeilings value because one grant key must be able to carry a `forward` ceiling and a DIFFERENT `turn`; the seat
    // authors it one gesture at a time (each gesture writes only the ordinals its mask names). It is never derived
    // from contributor rows — no combination across rows (max, sum, or min) is defensible, and the previous
    // narrow-to-the-minimum reading additionally made the effective ceiling change as contributors came and went.
    //
    // Both are written unconditionally by TryGrant on every accepted grant that carries one (last-write-wins, exactly
    // like a budget re-grant) and cleared by Revoke. Conflicts is what enforces WHICH row may carry WHICH; the tables
    // themselves store whatever TryGrant accepted, the same honesty split m_budgets keeps.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ChannelReachMask> m_channelReach = new();
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ChannelCeilings> m_poolCeilings = new();
    // The KIND-mask payload — a row's ceiling on WHICH mutation-kind ordinals it may dispatch, over the concrete
    // section subject it holds Mutate over or the concrete state row it holds Edit over. Same parallel-keyed-
    // structure precedent as m_budgets/m_channelReach above, with one deliberate asymmetry: WorldGrant.KindMask's own
    // doc names it — a null mask on a re-grant of the SAME row CLEARS a previously-recorded entry here, rather than
    // leaving it untouched (see TryGrant).
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), MutationKindMask> m_kindMasks = new();
    // The WRITE-mask payload — the cross-document durable-state channel's own vocabulary (WorldDocumentWriteKind),
    // on a Mutate row over a concrete state subject. A SEPARATE table from m_kindMasks, never a second reading of
    // it: the two masks share a bit-lane shape and nothing else, and one dictionary holding both would put
    // UpsertKit and Set on the same bit again (see MutationKindMask's own remarks). Same write/clear discipline.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), DocumentWriteMask> m_writeMasks = new();
    // The seeded per-section Mutate backdrop rows (principal + section subject), recorded at construction so rule (2)
    // can exempt them from blocking an exclusive acquisition — the concrete twin of the ordinary-`all` exemption. A
    // revoke deletes the marker (a later re-grant is a deliberate hold, not a seed).
    private readonly HashSet<SeededKey> m_seededSections = new();
    // The per-(principal, capability) handle-table cache (docs/capability-channels-plan.md's "Authority is a handle,
    // never a name") — lazily built on first request and reused after, since a table's own staleness check
    // (WorldHandleTable, keyed off m_revision below) is what decides whether it needs to re-project, not whether the
    // cache entry itself is fresh. WorldHandleTable's constructor refuses any principal but Addon/Peer, so this cache
    // can never seat one for Console or Seat — a principal that could grant itself anything gets no handle table at all.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability), WorldHandleTable> m_handleTables = new();
    // Bumped on every change to a principal's held subject sets — TryGrant, Revoke (when it actually removes
    // something), and the engagement-route helpers below that mutate the same per-principal Control storage directly.
    // A WorldHandleTable compares this against the revision it last rebuilt from, so "rebuilt when the grants it
    // projects change" costs a single int comparison on every tick that does not touch the grant table at all.
    private int m_revision;
    // The GROUP + MEMBERSHIP binding substrate's own two indices — resynced WHOLESALE from the live document's
    // `groups` section on every WorldServer.Install (boot, every mutation, every rebuild/undo), never incrementally
    // patched: a group/member count is capacity-bounded (WorldGroupCapacity), so a full rebuild is cheap, and a full
    // rebuild is what makes membership expansion CHECK-TIME rather than latched at grant time — the live table
    // Allows reads is always this tick's settled document, never a snapshot taken when a grant was authored.
    //
    // m_groupMembership: principal -> the group ids it CURRENTLY belongs to. Consulted by Allows' group-expansion
    // step below. FLAT ONLY means this is never itself keyed by a group principal on the value side.
    private readonly Dictionary<WorldPrincipal, List<string>> m_groupMembership = new();
    // m_groupReach: group id -> the capabilities at least one of its kind's declared roles reaches. Consulted by
    // TryGrant's reachability check (Conflicts) — the addon-reachability-honesty analog: granting a group principal a
    // capability no role of its kind could ever exercise is an admitted-but-inert grant, and this table is what lets
    // the door refuse it rather than lie.
    private readonly Dictionary<string, HashSet<WorldCapability>> m_groupReach = new();
    // m_ownedGroups: principal -> the group ids it CURRENTLY owns — resynced in the SAME wholesale pass as
    // m_groupMembership/m_groupReach, from the live document's `groups.ownership` rows. Consulted by Allows'
    // ownership-expansion fallback (composition-core's OWNERSHIP-CONSULT extension) — the OwnershipSubject.Group
    // half of Puck.World.WorldOwnership, resolved through the SAME "reach whatever the owned group's own rows hold"
    // shape m_groupMembership already gives a MEMBER, so an owner needs no membership row of its own. A
    // WorldOwnership row naming an OwnershipOwnerKind.Group owner (a group owns a group) resolves ONE level here,
    // at sync time, against that owning group's CURRENT roster: every one of ITS members is recorded as owning the
    // subject group too — flat, never recursive (a member is never itself a group; WorldDefinitionValidator's
    // IsLegitimateGroupMember already refuses that shape), so this can never cycle.
    private readonly Dictionary<WorldPrincipal, List<string>> m_ownedGroups = new();
    // m_driveGates: 0-based body entity index -> the name of the FIRST-in-document-order WorldStateRow declaring
    // GatesDrive whose own per-body cell (keyed by that index, entity-addressed the same way
    // WorldStateReader.ArgExtremum resolves a keyed cell key) currently reads nonzero. Resynced wholesale in the
    // SAME pass as the group substrate (SyncState, called alongside SyncGroups) — CC/death gating's own precomputed
    // index (composition-core's Seam A), consulted by WorldServer.ApplyIntentSubmission and world.why, never folded
    // into Allows itself: unlike ownership (which only ever ADDS reach, safe for every Allows caller), a drive gate
    // REFUSES, and several OTHER Drive/body callers (player.join/leave/identity, an administrator's own lookup) ask
    // a different question ("may this principal ever drive this body") that a temporary status effect must not
    // answer — see GrantRule.DriveGated's own remarks. THE RULE, for the next deciding fact someone wants the door
    // to consult: does it ADD reach or SUBTRACT it? An additive fact (ownership) folds into Allows safely — every
    // caller, capability lookups included, gets a superset and nothing spuriously refuses; a SUBTRACTIVE fact (a
    // status gate) must gate at the ingress, NEVER inside Allows, or it corrupts every capability query.
    private readonly Dictionary<int, string> m_driveGates = new();

    /// <summary>Seeds the permissive local-play defaults so boot behavior is UNCHANGED until someone revokes: every seat
    /// holds Drive over its OWN body only — never the wildcard (see <see cref="IsLegitimateSubject"/>'s remarks for why
    /// a Seat may still be GRANTED Drive/all LATER despite not being seeded with it) — plus
    /// Observe/Control/Mutate/Edit over its domain; the console holds Drive over ANY body (the table's only
    /// boot-seeded Drive/all) plus Observe/Control/Mutate/Edit over its domain. Each concrete peer generation receives
    /// ONLY Control over any screen when its <c>PeerAdmitted</c> event applies (population entries engage diegetic
    /// machines today, exactly like seats — the route capability, not Drive: peers do not submit intents, and get no
    /// Observe/Mutate/Edit at all). Addons get
    /// nothing — Observe is a newer capability and this posture extends the SAME deny-by-default rule to
    /// it, per the capability-channels plan's threat model: Console is fully trusted (grants are honesty, not
    /// security) and Seats are trusted locally (seeded permissive so local play is never gated on this until someone
    /// narrows trust); Addon and Peer are the untrusted side and get neither verb at seed. Mutate is seeded per-section
    /// (not the wildcard) so a single section can be revoked; Observe, Control, and Edit use the wildcard for
    /// every principal <see cref="SeedDomain"/> seeds (Console and every Seat); Drive is the one exception — per-body
    /// for a Seat, the wildcard only for Console.</summary>
    /// <param name="seatCount">The reserved local-seat count (each seat 0..seatCount-1 gets its default body grant).</param>
    /// <param name="population">The entity-table ceiling used to validate concrete body subjects.</param>
    /// <param name="routeTransition">The observer called after a principal's effective Control route changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routeTransition"/> is <see langword="null"/>.</exception>
    public WorldGrants(int seatCount, int population, Action<WorldPrincipal, GrantSubject?, GrantSubject?> routeTransition) {
        ArgumentNullException.ThrowIfNull(argument: routeTransition);

        m_population = population;
        m_routeTransition = routeTransition;

        for (var slot = 0; (slot < seatCount); slot++) {
            // Seat(slot) is right here: this establishes the SEAT identity's own boot grants, not an attribution of
            // someone else's action — there is no roster/claim to ask (this table exists before any claim can).
            var seat = WorldPrincipal.Seat(slot: slot);

            _ = TryGrant(grant: new WorldGrant(Principal: seat, Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: slot), Exclusive: false), reason: out _);
            SeedDomain(principal: seat);
        }

        SeedDomain(principal: WorldPrincipal.Console);
        _ = TryGrant(grant: new WorldGrant(Principal: WorldPrincipal.Console, Capability: WorldCapability.Drive, Subject: GrantSubject.All, Exclusive: false), reason: out _);

        // Peer authority is minted only when a concrete generation is admitted. Pre-seeding index-only peers here
        // would let a later occupant inherit an earlier session's authority.
    }

    /// <summary>Clears EVERY held row — concrete and wildcard holds, exclusive reservations, budgets, channel
    /// reach/ceilings, verb masks, the seeded-section marker set, and the handle-table cache — then re-seeds the
    /// permissive local-play defaults exactly as the constructor does, SILENTLY (via <see cref="TryGrant"/> directly,
    /// never the loud <c>Server.WorldServer.Grant</c> door — identical to how the constructor's own seed is silent).
    /// The runtime half of a whole-document rebuild (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>):
    /// "runtime grants drop; document grants re-apply as at boot." The document's OWN <c>Grants</c> section is
    /// deliberately NOT replayed here — that half needs <see cref="Server.WorldServer.WithoutAuthoredConsent"/> and
    /// the loud <c>Grant</c> door, so the caller replays it immediately afterward exactly as the constructor's own
    /// body does, and re-mints every currently-admitted peer connection's admission grant afterward still (a
    /// peer is a CONNECTION, not a document row or a boot-time seat, so nothing here or in the document replay
    /// re-establishes it).</summary>
    /// <param name="seatCount">The reserved local-seat count — identical to the value passed at construction.</param>
    public void Reset(int seatCount) {
        var droppedRoutes = new List<(WorldPrincipal Principal, GrantSubject Target)>();

        foreach (var (principal, grants) in m_byPrincipal) {
            if (grants.RouteTarget() is { } target) {
                droppedRoutes.Add(item: (principal, target));
            }
        }

        m_byPrincipal.Clear();
        m_exclusive.Clear();
        m_budgets.Clear();
        m_eventBudgets.Clear();
        m_holdCeilings.Clear();
        m_channelReach.Clear();
        m_poolCeilings.Clear();
        m_kindMasks.Clear();
        m_writeMasks.Clear();
        m_seededSections.Clear();
        m_handleTables.Clear();

        foreach (var (principal, target) in droppedRoutes) {
            NotifyRouteTransition(principal: principal, previous: target, current: null);
        }

        for (var slot = 0; (slot < seatCount); slot++) {
            var seat = WorldPrincipal.Seat(slot: slot);

            _ = TryGrant(grant: new WorldGrant(Principal: seat, Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: slot), Exclusive: false), reason: out _);
            SeedDomain(principal: seat);
        }

        SeedDomain(principal: WorldPrincipal.Console);
        _ = TryGrant(grant: new WorldGrant(Principal: WorldPrincipal.Console, Capability: WorldCapability.Drive, Subject: GrantSubject.All, Exclusive: false), reason: out _);
    }

    // Observe over every subject, Control over every screen, Mutate over every section EXCEPT
    // the meta-authority one (see the Grants skip below; the console alone is seeded over it), and Edit over the
    // `all` wildcard, which reaches every state ROW (Edit's domain is state:<name>) — the non-Drive permissive
    // defaults shared by seats and the console. Observe is ENFORCED (the addon read point checks it — see IsLegitimateSubject's remarks).
    private void SeedDomain(WorldPrincipal principal) {
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Observe, Subject: GrantSubject.All, Exclusive: false), reason: out _);
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false), reason: out _);
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Edit, Subject: GrantSubject.All, Exclusive: false), reason: out _);
        // The shared window-composition authority — seats and the console can drive the live view.override layout/view.override camera
        // overrides (peers, who get only Control/all above, do not receive this concrete grant). A director can still
        // acquire it exclusively over this concrete subject to own the shot.
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Control, Subject: GrantSubject.Composition, Exclusive: false), reason: out _);

        foreach (var section in Enum.GetValues<WorldSection>()) {
            // Grants is META-authority: mutating that section authors WHO MAY DO WHAT, applied at the next boot. The
            // permissive backdrop is a decision about convenience for a human at a controller — it was never a decision
            // to hand every seat the grant table, so only the console (an operator who could grant itself anything
            // anyway) is seeded over it. A seat that genuinely needs it is granted it deliberately, and the denial is
            // the ordinary loud per-section refusal. This is also why a seat cannot pass AllowsAllSections: a
            // whole-document swap rewrites the grant rows, which is exactly the authority a seat is not seeded.
            if ((section == WorldSection.Grants) && (principal.Kind != PrincipalKind.Console)) {
                continue;
            }

            var subject = GrantSubject.Section(section: section);

            _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Mutate, Subject: subject, Exclusive: false), reason: out _);
            // Mark the row as SEED so it never blocks another principal's exclusive section hold (see the type doc's
            // acquisition rules) — the backdrop must never block a reservation, exactly like the ordinary `all` wildcard.
            _ = m_seededSections.Add(item: new SeededKey(Principal: principal, Capability: WorldCapability.Mutate, Subject: subject));
        }
    }

    /// <inheritdoc/>
    /// <remarks>Membership expansion — grown INTO this ONE predicate rather than duplicated at each door, per the
    /// group+binding substrate's own design rule. Checked LAST, after the principal's own concrete/wildcard rows
    /// miss: a principal's own hold always wins first, and a group's hold is a FALLBACK, never an override. FLAT
    /// ONLY means this never recurses — a group entry in <see cref="m_groupMembership"/>'s value list is itself
    /// checked by looking up ITS OWN rows only, never by treating the group AS a further member of anything.
    /// <para>OWNERSHIP-CONSULT (composition-core): the SAME fallback shape, grown a SECOND way, checked AFTER
    /// membership — an ownership binding is a deciding FACT this door consults, never a grant row
    /// <see cref="WorldGrants"/> mints; <c>Puck.World.WorldOwnership</c> seeds/implies authority, it never IS a
    /// grant. Safe to fold unconditionally into every <see cref="Allows"/> caller (unlike Seam A's drive gate,
    /// which REFUSES and is therefore scoped to the intent-admission door alone — see
    /// <see cref="GrantRule.DriveGated"/>): ownership only ever ADDS reach, so no existing caller's denial can flip
    /// to an unwanted allow, and no existing caller's allow is ever taken away.</para></remarks>
    public GrantVerdict Allows(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        // The exclusivity override: a reserved subject answers for its reserver ALONE, so an exclusively-held body has
        // exactly one effective driver even though the console still holds the seeded Drive/all wildcard.
        if (ExclusiveHolderOf(capability: capability, subject: subject) is { } holder) {
            return ((holder == principal)
                ? new GrantVerdict(Rule: GrantRule.ReserverMatch)
                : new GrantVerdict(Rule: GrantRule.BeatenByReserver, Reserver: holder));
        }

        if (m_byPrincipal.TryGetValue(key: principal, value: out var grants) && (grants.For(capability: capability) is { } subjects)) {
            if (subjects.Contains(item: subject)) {
                return new GrantVerdict(Rule: GrantRule.ConcreteHold);
            }

            if (subjects.Contains(item: GrantSubject.All)) {
                return new GrantVerdict(Rule: GrantRule.WildcardHold);
            }
        }

        // The group-expansion fallback: does a group `principal` is CURRENTLY a member of hold this (capability,
        // subject) itself? Read fresh from m_groupMembership — this tick's synced snapshot of the live document —
        // never from anything recorded when a grant was authored, which is what makes a departed member's hold
        // evaporate on its very next check rather than staying latched.
        if (m_groupMembership.TryGetValue(key: principal, value: out var memberOf)) {
            foreach (var groupId in memberOf) {
                var groupPrincipal = WorldPrincipal.Group(id: groupId);

                if (!m_byPrincipal.TryGetValue(key: groupPrincipal, value: out var groupGrants) || (groupGrants.For(capability: capability) is not { } groupSubjects)) {
                    continue;
                }

                if (groupSubjects.Contains(item: subject) || groupSubjects.Contains(item: GrantSubject.All)) {
                    return new GrantVerdict(Rule: GrantRule.GroupHold, Group: groupId);
                }
            }
        }

        // The ownership-expansion fallback: does a group `principal` CURRENTLY owns (directly, or transitively
        // through owning-group membership — both resolved at sync time into m_ownedGroups) hold this (capability,
        // subject) itself? Read fresh, same as the membership fallback above — a transferred or revoked ownership
        // evaporates on its very next check.
        if (m_ownedGroups.TryGetValue(key: principal, value: out var ownedOf)) {
            foreach (var groupId in ownedOf) {
                var groupPrincipal = WorldPrincipal.Group(id: groupId);

                if (!m_byPrincipal.TryGetValue(key: groupPrincipal, value: out var groupGrants) || (groupGrants.For(capability: capability) is not { } groupSubjects)) {
                    continue;
                }

                if (groupSubjects.Contains(item: subject) || groupSubjects.Contains(item: GrantSubject.All)) {
                    return new GrantVerdict(Rule: GrantRule.OwnershipHold, Group: groupId);
                }
            }
        }

        return new GrantVerdict(Rule: GrantRule.NoHold);
    }

    /// <summary>Resyncs the group+membership+ownership index WHOLESALE from the live document's <c>groups</c>
    /// section — called unconditionally by <c>WorldServer</c> on every construction and every <c>Install</c> (boot,
    /// every mutation, every rebuild/undo), the one choke point every document swap already passes through. Cheap:
    /// group, per-kind member, and ownership counts are all capacity-bounded (<c>WorldGroupCapacity</c>).</summary>
    /// <param name="groups">The live document's group roster rows.</param>
    /// <param name="kinds">The live document's declared group-kind catalog.</param>
    /// <param name="ownership">The live document's ownership bindings.</param>
    public void SyncGroups(IReadOnlyList<WorldGroup> groups, IReadOnlyList<WorldGroupKind> kinds, IReadOnlyList<WorldOwnership> ownership) {
        m_groupMembership.Clear();
        m_groupReach.Clear();
        m_ownedGroups.Clear();

        var reachByKindName = new Dictionary<string, HashSet<WorldCapability>>(comparer: StringComparer.Ordinal);

        foreach (var kind in kinds) {
            var reach = new HashSet<WorldCapability>();

            foreach (var role in kind.Roles) {
                foreach (var capability in role.Capabilities) {
                    _ = reach.Add(item: capability);
                }
            }

            reachByKindName[kind.Name] = reach;
        }

        var groupsById = new Dictionary<string, WorldGroup>(comparer: StringComparer.Ordinal);

        foreach (var group in groups) {
            m_groupReach[group.Id] = (reachByKindName.TryGetValue(key: group.KindName, value: out var reach) ? reach : new HashSet<WorldCapability>());
            groupsById[group.Id] = group;

            foreach (var member in group.Members) {
                if (!m_groupMembership.TryGetValue(key: member, value: out var memberOf)) {
                    memberOf = new List<string>();
                    m_groupMembership[member] = memberOf;
                }

                memberOf.Add(item: group.Id);
            }
        }

        // Ownership is NOT a grant — it is a deciding FACT the door consults (see GrantRule.OwnershipHold). Only
        // Subject.Kind Group exists today (WorldOwnershipSubjectKind's own remarks); a later lane's item/instance
        // subject widening adds its own case here rather than reusing this one.
        foreach (var row in ownership) {
            if (row.Subject.Kind != OwnershipSubjectKind.Group) {
                continue;
            }

            switch (row.Owner.Kind) {
                case OwnershipOwnerKind.Principal:
                    if (row.Owner.Principal is { } ownerPrincipal) {
                        AddOwnedGroup(owner: ownerPrincipal, groupId: row.Subject.Id);
                    }

                    break;
                case OwnershipOwnerKind.Group:
                    // A group owns a group: every CURRENT member of the owning group reaches the SUBJECT group's own
                    // rows too — one level, resolved here against this same pass's roster, never recursively (a
                    // member is never itself a group).
                    if ((row.Owner.GroupId is { } ownerGroupId) && groupsById.TryGetValue(key: ownerGroupId, value: out var ownerGroup)) {
                        foreach (var member in ownerGroup.Members) {
                            AddOwnedGroup(owner: member, groupId: row.Subject.Id);
                        }
                    }

                    break;
            }
        }
    }

    private void AddOwnedGroup(WorldPrincipal owner, string groupId) {
        if (!m_ownedGroups.TryGetValue(key: owner, value: out var ownedOf)) {
            ownedOf = new List<string>();
            m_ownedGroups[owner] = ownedOf;
        }

        ownedOf.Add(item: groupId);
    }

    /// <summary>Resyncs the drive-admission gate index WHOLESALE from the live document's <c>state</c> section —
    /// called alongside <see cref="SyncGroups"/> at the SAME choke points (construction, every <c>Install</c>), so a
    /// live <c>world.state.cell.set</c> that flips a gate row's cell is settled before the NEXT tick's intent drain
    /// reads it (composition-core's CC/DEATH GATING, Seam A). Resolves each candidate cell through
    /// <see cref="WorldStateReader.TryRead"/> — the section's ONE (row, key) read seam — rather than a bespoke scan
    /// of <see cref="WorldStateCell.Value"/>, exactly the discipline the entity-addressable reductions already
    /// follow. The tick this resolves at is inert for every row this index can ever hold:
    /// <see cref="WorldStateRow.GatesDrive"/> requires a declared <see cref="WorldStateRow.Capacity"/>
    /// (WorldDefinitionValidator), and <see cref="WorldStateRow.Advance"/> — the only trait TryRead's tick argument
    /// affects — refuses beside one, so a gate row can never advance; <c>0</c> reads identically to any other tick.
    /// First-in-document-order gate wins a body (declaration-order tiebreak, the same convention same-tick rule
    /// effects resolve by).</summary>
    /// <param name="definition">The live document.</param>
    public void SyncState(WorldDefinition definition) {
        m_driveGates.Clear();

        foreach (var row in definition.State) {
            if (!row.GatesDrive) {
                continue;
            }

            foreach (var cell in (row.Cells ?? [])) {
                if (!int.TryParse(s: cell.Key, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var bodyIndex) || (bodyIndex < 0) || m_driveGates.ContainsKey(key: bodyIndex)) {
                    continue;
                }

                if (WorldStateReader.TryRead(definition: definition, rowName: row.Name, key: cell.Key.Value, tick: 0UL, row: out _, rawValue: out var raw, text: out _) && (raw is { } value) && (value != 0)) {
                    m_driveGates[bodyIndex] = row.Name;
                }
            }
        }
    }

    /// <summary>Determines whether <paramref name="bodyIndex"/> is currently DRIVE-GATED — carries a nonzero cell on a state row
    /// declaring <see cref="WorldStateRow.GatesDrive"/> — and, when it is, which row decided it. Checked fresh every
    /// call against the index <see cref="SyncState"/> last resynced; never latched.</summary>
    /// <param name="bodyIndex">The 0-based entity index to check.</param>
    /// <param name="gateRow">The deciding row's name, when gated; empty otherwise.</param>
    /// <returns><see langword="true"/> when the body is gated.</returns>
    public bool TryGetDriveGate(int bodyIndex, out string gateRow) {
        if (m_driveGates.TryGetValue(key: bodyIndex, value: out var found)) {
            gateRow = found;

            return true;
        }

        gateRow = string.Empty;

        return false;
    }

    // The principal that exclusively reserves `subject` for `capability`, considering the `all` wildcard reservation (an
    // exclusive `all` reserves every concrete subject of the capability). Null when the subject is unreserved — the
    // normal wildcard/subject-set logic then applies. A query for `all` itself only matches an EXACT `all` reservation:
    // a concrete exclusive body does not lock the whole-domain query the permissive Edit check makes.
    private WorldPrincipal? ExclusiveHolderOf(WorldCapability capability, GrantSubject subject) {
        if (m_exclusive.TryGetValue(key: new ExclusiveKey(Capability: capability, Subject: subject), value: out var exact)) {
            return exact;
        }

        if ((subject.Kind != GrantSubjectKind.All) &&
            m_exclusive.TryGetValue(key: new ExclusiveKey(Capability: capability, Subject: GrantSubject.All), value: out var wildcard)) {
            return wildcard;
        }

        return null;
    }

    /// <summary>Determines whether <paramref name="principal"/> may ADMINISTER (grant or revoke) <paramref name="capability"/> over
    /// <paramref name="subject"/> — the <c>world.grant</c>/<c>world.revoke</c> actor test, distinct from
    /// <see cref="Allows"/>. Enforced ONLY for principal kinds outside the trust boundary
    /// (<see cref="PrincipalKind.Addon"/>, <see cref="PrincipalKind.Peer"/>): those must hold the administered
    /// <c>(capability, subject)</c> themselves (ignoring exclusivity — see below), so a delegated administrator can
    /// never hand out authority it does not itself have. <see cref="PrincipalKind.Console"/> and
    /// <see cref="PrincipalKind.Seat"/> pass UNCONDITIONALLY: both sit inside the trust boundary (docs/capability-channels-plan.md's
    /// principal/adversary table — an operator who can already grant themselves anything), so gating self-administration
    /// there is ceremony that costs real functionality (self-revocation would become a one-way ratchet; a seeded
    /// per-section grant could never be re-issued as the `all` wildcard) and buys nothing while the actor stays a
    /// caller-asserted parameter hardcoded at `WorldGrantCommandModule`'s one call site — this stops being inert the
    /// day a `Peer` (or an untrusted `Addon`) can reach `IServerLink.SubmitGrant`/`SubmitRevoke` directly.
    ///
    /// For the kinds this DOES gate: membership ignores exclusivity, because <see cref="Allows"/>'s exclusivity
    /// override answers "does this principal effectively wield the capability right now" — correct for USE, wrong for
    /// ADMINISTRATION, where the principal who granted an exclusive hold must still be able to revoke it.</summary>
    /// <param name="principal">The acting identity administering the grant (the actor, never the grant's subject).</param>
    /// <param name="capability">The capability the administered grant confers.</param>
    /// <param name="subject">The subject the administered grant scopes to.</param>
    /// <remarks><b>NARROWED 2026-08, landed in the same change as consent-as-grant, because the two facts collide in
    /// both directions and neither is safe alone.</b> <see cref="PrincipalKind.Console"/> still passes unconditionally — it sits inside the
    /// trust boundary and gating self-administration there is ceremony, exactly as before. <see cref="PrincipalKind.Seat"/>
    /// no longer does: co-driving consent IS a grant row (<see cref="WorldGrant.Consent"/>/<see cref="WorldGrant.Ceiling"/>),
    /// so an unconditional Seat pass would let the enable flow work only because this door was wider than the feature
    /// needed — riding an acknowledged escalation risk (Open Decision 4) rather than closing it. A Seat may now
    /// administer a grant row ONLY where the subject is its own body — any capability (Drive, Observe, or Control)
    /// naming its own seat index, since <see cref="IsOwnSeatBody"/> is capability-blind — exactly what "enabling an
    /// addon on your own seat is the consent gesture" requires and nothing beyond
    /// it. This is a real behavior change: a Seat actor administering ANY other subject (another seat's body, a
    /// section, a screen, a state row) now refuses where it previously passed.</remarks>
    public bool HoldsForAdministration(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        if (principal.Kind == PrincipalKind.Console) {
            return true;
        }

        if (principal.Kind == PrincipalKind.Seat) {
            return IsOwnSeatBody(principal: principal, subject: subject);
        }

        if (!m_byPrincipal.TryGetValue(key: principal, value: out var grants)) {
            return false;
        }

        var subjects = grants.For(capability: capability);

        return (subjects is not null) && (subjects.Contains(item: GrantSubject.All) || subjects.Contains(item: subject));
    }

    /// <inheritdoc/>
    public bool AllowsAllSections(WorldPrincipal principal, WorldCapability capability, out WorldSection deniedSection, out GrantVerdict denial) {
        foreach (var section in Enum.GetValues<WorldSection>()) {
            if (Allows(principal: principal, capability: capability, subject: GrantSubject.Section(section: section)) is { IsAllowed: false } verdict) {
                deniedSection = section;
                denial = verdict;

                return false;
            }
        }

        deniedSection = default;
        denial = default;

        return true;
    }

    /// <summary>Adds a grant, enforcing exclusivity in BOTH orders. An incoming EXCLUSIVE grant over the wildcard
    /// <see cref="GrantSubject.All"/> is rejected outright (an exclusive reservation must name a concrete subject). A
    /// grant is REJECTED outright when its subject is not one its CAPABILITY legitimately admits (see
    /// <see cref="IsLegitimateSubject"/> for the full per-capability table and why): a concrete subject in the
    /// capability's own domain (a body for Drive, a screen for Control, a section for Mutate, a state row for Edit) is
    /// legitimate for ANY principal, while the wildcard is legitimate only for a principal already inside the local
    /// trust boundary for that capability (generally Console/Seat; Peer additionally for Control, per its own boot
    /// seed). A Drive grant's concrete <see cref="GrantSubjectKind.Body"/> is additionally bounded to an index the
    /// population actually holds. The grant is REJECTED when a DIFFERENT principal already holds a conflicting
    /// exclusive reservation of an overlapping subject (whether the incoming grant is exclusive or ordinary), or when
    /// an incoming EXCLUSIVE grant would share the same concrete subject with a different principal's ordinary hold.
    /// The wildcard <see cref="GrantSubject.All"/> ORDINARY grant is exempt from the exclusivity-conflict check in
    /// BOTH DIRECTIONS — an existing exclusive concrete hold never blocks a later ordinary wildcard re-grant, and the
    /// seeded wildcard never blocks a later exclusive concrete acquisition — so the permissive local defaults can
    /// always be narrowed and re-widened regardless of what exclusive holds exist elsewhere; enforcement
    /// (<see cref="Allows"/>) makes the exclusive holder the sole effective owner of ITS OWN subject regardless.
    /// Re-granting a subject the SAME principal already holds is idempotent (an upgrade to exclusive still records the
    /// reservation). NOT part of <see cref="IWorldGrantsView"/>: this is an authority door, reached only through
    /// <see cref="WorldServer.Grant"/>'s <see cref="HoldsForAdministration"/> actor check, never through the view a
    /// non-<see cref="WorldServer"/> caller holds.</summary>
    /// <param name="grant">The grant to add.</param>
    /// <param name="reason">On rejection, the human-readable reason; empty on success.</param>
    /// <returns><see langword="true"/> when the grant was added.</returns>
    public bool TryGrant(WorldGrant grant, out string reason) {
        if (Conflicts(grant: grant, reason: out reason)) {
            return false;
        }

        var priorRoute = ((grant.Capability == WorldCapability.Control)
            ? (m_byPrincipal.TryGetValue(key: grant.Principal, value: out var priorGrants) ? priorGrants.RouteTarget() : null)
            : null);

        if (grant.Exclusive) {
            m_exclusive[new ExclusiveKey(Capability: grant.Capability, Subject: grant.Subject)] = grant.Principal;
        }

        if (grant.Budget is { } budget) {
            // Unconditional, last-write-wins: a re-grant naming a different budget IS the budget-update verb — no
            // separate grammar exists to change one in place.
            m_budgets[(grant.Principal, grant.Capability, grant.Subject)] = budget;
        }

        if (grant.EventBudget is { } eventBudget) {
            // Same last-write-wins shape as Budget, over the SAME key — the two are independent siblings, never one
            // widening the other.
            m_eventBudgets[(grant.Principal, grant.Capability, grant.Subject)] = eventBudget;
        }

        if (grant.HoldCeiling is { } holdCeiling) {
            m_holdCeilings[(grant.Principal, grant.Capability, grant.Subject)] = holdCeiling;
        }

        var key = (grant.Principal, grant.Capability, grant.Subject);

        if ((grant.Consent is { } consent) && (grant.Ceiling is { } authoredCeiling)) {
            // The SEAT'S OWN ceiling gesture (Conflicts already refused this pair anywhere else): write the number
            // onto exactly the ordinals the mask names and leave every other ordinal as it was, so a second gesture
            // can give `turn` a different ceiling than `forward` without erasing the first. Revoke clears the value.
            ref var ceilings = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_poolCeilings, key: key, exists: out _);

            ceilings = ceilings.WithCeiling(channels: consent, ceiling: authoredCeiling);
        } else if (grant.Reach is { } reach) {
            // A contributor's REACH, last-write-wins exactly like a budget re-grant: a re-grant naming a different
            // channel set IS the reach-update verb, no new grammar needed.
            m_channelReach[key] = reach;
        }

        if (grant.KindMask is { } kindMask) {
            // Unconditional, last-write-wins — the same shape as m_budgets/m_channelReach above.
            m_kindMasks[key] = kindMask;
        } else if (CarriesKindMask(capability: grant.Capability, subject: grant.Subject)) {
            // THE ONE ASYMMETRY: WorldGrant.KindMask's own doc names it — a re-grant of a maskable row that carries
            // NO mask CLEARS a previously-recorded one, rather than leaving it untouched the way an omitted
            // Budget/Reach does. A mask a re-grant does not repeat is a mask the operator meant to take back;
            // silently surviving a re-grant that dropped it would be the opposite of what the operator typed.
            _ = m_kindMasks.Remove(key: key);
        }

        if (grant.WriteMask is { } writeMask) {
            m_writeMasks[key] = writeMask;
        } else if (CarriesWriteMask(capability: grant.Capability, subject: grant.Subject)) {
            _ = m_writeMasks.Remove(key: key);
        }

        ref var grants = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_byPrincipal, key: grant.Principal, exists: out _);

        grants.Add(capability: grant.Capability, subject: grant.Subject);
        m_revision++;

        if (grant.Capability == WorldCapability.Control) {
            NotifyRouteTransition(principal: grant.Principal, previous: priorRoute, current: grants.RouteTarget());
        }

        return true;
    }

    /// <summary>Determines whether a principal sits inside the LOCAL TRUST BOUNDARY for administration and metering — the
    /// boundary the grant door's budget/mask requirements and <see cref="WorldServer.TryAdmitMutation"/>'s budget gate
    /// both read.</summary>
    /// <remarks>Written as the POSITIVE admission list (Console and Seat) rather than "untrusted" as a named
    /// exclusion, so a principal kind added later is untrusted by the COMPLEMENT and can never slip through as
    /// trusted-by-omission. This is NOT the fold's contributor-trust predicate: that one keys on HOST LOCUS and
    /// counts a document-mounted <see cref="PrincipalKind.Addon"/> as trusted (see
    /// <c>WorldServer.StageContribution</c>). Conflating the two is the wrong-answer trap this repository's authority
    /// notes name explicitly — they diverge on <see cref="PrincipalKind.Addon"/>.</remarks>
    /// <param name="principal">The principal to classify.</param>
    /// <returns><see langword="true"/> for <see cref="PrincipalKind.Console"/> and <see cref="PrincipalKind.Seat"/>.</returns>
    public static bool IsTrusted(WorldPrincipal principal) => (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);

    // Whether an incoming grant conflicts with an existing hold under the exclusivity rule. Grant/revoke is a
    // human-cadence op (never the tick path), so the two scans are affordable; both are skipped entirely for the common
    // idempotent re-grant (a matching holder is the incoming principal itself).
    private bool Conflicts(WorldGrant grant, out string reason) {
        reason = string.Empty;

        // (-1) The WORLD holds no rows, ever. Its authority is STRUCTURAL — WorldServer.TryAdmitMutation admits it
        //      before consulting this table at all — so a row naming it would be accepted-and-inert, which is exactly
        //      the phantom-grant shape the table's key discipline exists to prevent. Refused BY NAME, in both the
        //      console door and the document `grants` replay, so an author who tries learns why rather than watching
        //      a row do nothing.
        if (grant.Principal.Kind == PrincipalKind.World) {
            reason = "the world's own authored program holds no grants — its authority is structural (a rule's effects and a kit's generate effect are the document acting on itself, never an actor submitting); a row here would be accepted and inert";

            return true;
        }

        // (-1b) A DOCUMENT holds no LIVE rows either, for the mirror-image reason: its capability is real, but it is
        //       read off the OWNER'S DOCUMENT (Server.WorldOwnedWorlds.Decide/TryReadDurableState consult
        //       `definition.Grants` directly), never off this table. A live row for one is budget-less, mask-less,
        //       and consulted by nothing — the same phantom shape rule (-1) refuses, reached from the other side.
        //       This is why the two document-`grants` REPLAYS (the constructor's and the rebuild's) skip Document
        //       rows rather than feeding them here: a document legitimately CARRIES them, so refusing them at the
        //       replay would print a loud rejection for correct authored data. `world.grant` cannot reach here at all
        //       (the wire has no live discriminant for the kind), so this door is what closes the programmatic path
        //       and keeps `world.grants` free of a row nothing enforces. `world.grants document:<id>` echoes the
        //       document-authored rows instead, where they actually live.
        if (grant.Principal.Kind == PrincipalKind.Document) {
            reason = "a document holds no LIVE grants — the cross-document durable-state write-back channel reads its rows off the OWNER'S DOCUMENT (world.grant.set authors them, world.grants document:<id> echoes them), so a row here would be accepted and inert";

            return true;
        }

        // (0) An exclusive reservation MUST name a concrete subject. An "exclusively own everything" claim (exclusive
        //     `all`) has no legitimate consumer today and is order-dependently dishonest: on a table with a prior
        //     concrete hold the reverse order rejects, while this order slips past acquisition and then denies EVERY
        //     concrete seat at enforcement. Reject it outright — in BOTH orders and on a fresh table — so an exclusive
        //     hold always means one named subject. The ordinary `all` wildcard (the permissive backdrop) is untouched.
        if (grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All)) {
            reason = $"an exclusive {Label(capability: grant.Capability)} reservation must name a concrete subject (exclusive 'all' is not allowed)";

            return true;
        }

        // (0b) EVERY capability accepts only the subject shapes it legitimately admits — expressed as the POSITIVE
        //      rule in IsLegitimateSubject (which shapes ARE legitimate) rather than a list of rejected kinds, so a
        //      future GrantSubjectKind OR WorldCapability is refused by default instead of silently admitted: either
        //      has to be added there deliberately. Letting an illegitimate subject through the table is not a
        //      harmless no-op: each capability's enforcement site only ever matches its own real shape (e.g.
        //      a Drive handle table only ever projects Body subjects, HoldsRoute matches Screen/Body route subjects only), so an illegitimate
        //      grant (a Drive over a screen, a Control over a section, a Mutate over a body, an Edit over a body...)
        //      resolves to NO enforceable authority at all, yet world.grants would still render a live row for a
        //      principal holding zero authority — exactly the honesty failure this rule exists to prevent. THIS is
        //      also the ONLY thing standing between an exclusive grant over one of those illegitimate subjects and
        //      m_exclusive: once reserved there, rule (1) below would treat it as a real exclusive hold like any
        //      other. (Rule (1)'s own symmetric wildcard exemption — not this shape gate — is what stops a
        //      LEGITIMATE exclusive concrete hold, e.g. 'drive body:5 exclusive', from permanently blackholing a
        //      seeded wildcard; see its remarks.) Reject an illegitimate shape loudly at the boundary where the
        //      operator can see why, rather than silently seating a principal that holds nothing enforceable.
        if (!IsLegitimateSubject(principal: grant.Principal, capability: grant.Capability, subject: grant.Subject)) {
            reason = SubjectRule(principal: grant.Principal, capability: grant.Capability, subject: grant.Subject);

            return true;
        }

        // (0b-2) THE GROUP-REACHABILITY HONESTY RULE — the addon-reachability analog this substrate's own design
        //        calls for: a grant naming a GROUP principal must not silently accept authority no role of its kind
        //        could ever exercise (an admitted-but-inert grant is a grant that lies, the identical posture
        //        IsLegitimateSubject's own remarks give for an illegitimate subject shape). m_groupReach is resynced
        //        wholesale on every document swap (SyncGroups), so this reads the CURRENT declared kind, never a
        //        snapshot from when the group was formed.
        if ((grant.Principal.Kind == PrincipalKind.Group) &&
            (!m_groupReach.TryGetValue(key: grant.Principal.Name ?? string.Empty, value: out var reach) || !reach.Contains(item: grant.Capability))) {
            reason = (m_groupReach.ContainsKey(key: grant.Principal.Name ?? string.Empty)
                ? $"{grant.Principal.Describe()}'s kind declares no role reaching {Label(capability: grant.Capability)} — granting it would be accepted-and-inert; widen the kind's roles instead"
                : $"{grant.Principal.Describe()} does not exist — form it first, or author its kind");

            return true;
        }

        // (0c) The BUDGET field — the plan's compute-not-space axis (a request costs a host dispatch, not a record in
        //      a region, so a guest filling its request quota every tick is a CPU denial of service no space budget
        //      describes). Three loud-completeness rules, none of them a default:
        //      - `budget:0` is refused UNCONDITIONALLY — a granted-but-never-dispatched row is accepted-and-inert (the
        //        admission-degenerate rule); grant nothing instead. `WorldGrantCommandModule.TryParseGrant` now
        //        refuses this same shape at PARSE TIME for a console-typed `world.grant`/`world.grant.set`, so this
        //        rule's live role today is a document-authored `WorldDefinition.Grants` row (JSON deserializes
        //        `"budget": 0` straight into the field, bypassing the parser entirely) — this door is what still
        //        refuses THAT loudly, at boot.
        //      - An `Observe` or `Drive` grant to an UNTRUSTED principal (Addon/Peer) WITHOUT a budget is refused: a
        //        defaulted budget would silently decide a DoS ceiling on the operator's behalf. Both doors meter the
        //        SAME shape of cost — a guest's own compute dispatched per tick, per subject — so both carry the
        //        requirement.
        //      - A budget on anything else — a TRUSTED principal's grant (Console/Seat), or a capability that is not
        //        `Observe`/`Drive` — is refused naming the posture: trusted reads/drives are unmetered
        //        (`WorldServer.Answer`, the draw paths, and a Console/Seat `Drive` submission all stay ungated), and
        //        no capability but Observe and Drive has a dispatch door to meter yet, so admitting the field there
        //        now would be a lie the enforcement cannot back — greenfield makes admitting it LATER free, so there
        //        is no cost to waiting until the door exists. Untrusted is checked before capability so the
        //        metered-vs-other split (not principal trust) is what the message leads with; the order does not
        //        change which grants are refused.
        //
        //      TRUSTED is written as the POSITIVE admission list, not "untrusted" as a named exclusion — the same
        //      discipline `IsLegitimateSubject`'s own remarks give for why a future `GrantSubjectKind`/
        //      `WorldCapability` is refused by default rather than silently admitted: Console and Seat are the two
        //      kinds inside the local trust boundary today, and every other kind — Addon, Peer, and any kind added
        //      later — is untrusted by the COMPLEMENT, never by name, so a future principal kind cannot slip through
        //      as trusted-by-omission and dispatch an Observe/Drive grant unmetered. Behavior for today's four kinds
        //      is unchanged: Console/Seat trusted, Addon/Peer untrusted, exactly as the prior negative test read.
        //      METERED is likewise the positive list (Observe, Drive) rather than a named exclusion, for the
        //      identical reason: a future capability's dispatch door is admitted here deliberately, never by falling
        //      through.
        // Mutate joined the metered positive list alongside Observe/Drive: an untrusted principal's dispatch through
        // the mutation door (the addon seam's pre-flight, a peer's submission) is host compute exactly like an Observe
        // read or a Drive fold, so it owes the identical denial-of-service ceiling — WorldServer.TryAdmitMutation is
        // the one gate that charges it.
        //
        // Mutate is metered on its DISPATCH LANE ONLY — a concrete section:<name>, the subject that door dispatches
        // through. Mutate over a concrete state:<name> is the OTHER lane entirely: the cross-document durable-state
        // write-back channel (Server.WorldOwnedWorlds.Decide), which has no dispatch door at all — it runs off the
        // simulation's own per-tick durable-state outputs and is gated by a WRITE mask, not by an allowance. A budget
        // there would be a field nothing reads, so it is refused by name exactly as a budget on Control or Edit is,
        // rather than demanded and then ignored. (The wildcard needs no arm: IsLegitimateSubject already confines an
        // untrusted Mutate grant to a concrete subject.)
        var untrustedPrincipal = !IsTrusted(principal: grant.Principal);
        var meteredMutate = ((grant.Capability == WorldCapability.Mutate) && (grant.Subject.Kind == GrantSubjectKind.Section));
        var metered = ((grant.Capability is WorldCapability.Observe or WorldCapability.Drive) || meteredMutate);

        if (grant.Budget == 0) {
            reason = "budget:0 is refused — a granted-but-never-dispatched row is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (metered && untrustedPrincipal && (grant.Budget is null)) {
            reason = $"an untrusted {Label(capability: grant.Capability)} grant to {grant.Principal.Describe()} requires an explicit budget:<n> — a defaulted per-tick dispatch allowance would silently decide a denial-of-service ceiling";

            return true;
        }

        if (grant.Budget is not null) {
            if (!metered) {
                reason = $"budget is refused on {Label(capability: grant.Capability)} {grant.Subject.Describe()} — only observe, drive, and mutate over a concrete section:<name> pass through a dispatch door there is anything to meter (a mutate state:<name> row is the cross-document write-back channel, gated by writes:<name,...> rather than by an allowance)";

                return true;
            }

            if (!untrustedPrincipal) {
                reason = $"budget is refused on {grant.Principal.Describe()}'s grant — trusted reads/drives are unmetered (console/seat, WorldServer.Answer, and the draw paths all stay ungated)";

                return true;
            }
        }

        // (0c-3) THE VERB MASK IS REQUIRED on an untrusted principal's Mutate row over a concrete section — the same
        //        accepted-and-inert discipline `budget:0` above already states, applied to the OTHER half of what
        //        makes an untrusted mutation row honest. This is a deliberate NARROWING: a row shape an operator
        //        could author yesterday is illegal today.
        //
        //        The reason it belongs HERE and not at the enforcement door: the admission predicate
        //        (WorldServer.TryAdmitMutation) reads an ABSENT kind mask as FULL REACH, and it must — Console's boot
        //        seed hands it maskless Mutate/section:<s> rows for every section, so refuse-all-on-unmasked there
        //        would deny every trusted mutation in the engine. The two readings cannot both be right at one door,
        //        so the strictness moves to the door that can afford it: an untrusted row must SAY which kinds it
        //        reaches, and a row that does not say is refused before it exists. That is what makes "absent means
        //        full reach" safe rather than a hole — an untrusted principal can never hold an absent mask.
        //
        //        Scoped to the SECTION subject deliberately. An untrusted Mutate row over a concrete state:<name> is
        //        the CROSS-DOCUMENT durable-state channel (Server.WorldOwnedWorlds.Decide), whose vocabulary is
        //        WorldDocumentWriteKind and whose mask — `writes:` — is already REQUIRED by that door as the whole of
        //        what admits a foreign write. Demanding a mutation-KIND mask there would demand the wrong lane's mask
        //        (see MutationKindMask's own remarks on the duality this split closed).
        // (0c-3b) OWNER RULING: an UNTRUSTED principal (Addon/Peer) is REFUSED Mutate over section:rules outright —
        //         a NAMED NARROWING, not a mask requirement, and it sits beside the maskless-untrusted refusal below
        //         because both answer "what makes an untrusted mutation row honest".
        //
        //         Every OTHER untrusted mutation row is metered: a budget bounds its dispatches per tick and a verb
        //         mask bounds which kinds it reaches. A rules row escapes both, because what it authors is not a
        //         mutation — it is a PROGRAM, and that program's own effects act as WorldPrincipal.World, which
        //         TryAdmitMutation admits STRUCTURALLY, before this table is consulted at all and with no budget
        //         charged. So one gated act (authoring one rule) buys unbounded ungated writes forever after: every
        //         budget the operator authored is laundered through it. A verb mask cannot close that — the mask
        //         governs which kinds the ROW may dispatch, and the rule's effects are not dispatched by the row.
        //         The narrowing is the only honest answer while the world principal stays structurally exempt.
        //
        //         Trusted principals (Console/Seat) are unaffected: they are inside the local trust boundary, hold
        //         Mutate over every section at seed (Console over Grants too), and could grant themselves anything
        //         regardless — gating them here would be ceremony, exactly as it is at HoldsForAdministration.
        // WorldSection.Interactions carries the IDENTICAL laundering risk (0c-3b's own reasoning, verbatim): an
        // interaction desugars into a synthesized rule (WorldRuleCompiler.CompileAllInteractions), and its EFFECTS
        // fire through the SAME FireWorldRuleEffect path — WorldPrincipal.World, admitted structurally, unbounded,
        // unmetered. Authoring an interaction is therefore exactly as much of a one-gated-act laundering door as
        // authoring a rule is, so the narrowing widens to both subjects rather than leaving the newer one open.
        if (untrustedPrincipal && (grant.Capability == WorldCapability.Mutate) &&
            (grant.Subject.Kind == GrantSubjectKind.Section) &&
            (((WorldSection)grant.Subject.Value == WorldSection.Rules) || ((WorldSection)grant.Subject.Value == WorldSection.Interactions))) {
            var subjectNoun = (((WorldSection)grant.Subject.Value == WorldSection.Rules) ? "rule" : "interaction");

            reason = $"an untrusted mutate grant to {grant.Principal.Describe()} over {grant.Subject.Describe()} is refused — a {subjectNoun}'s EFFECTS act as the world's own program, which the admission door admits structurally and never meters, so authoring a {subjectNoun} launders every budget and verb mask this row carries through one gated act; a verb mask cannot bound what the row does not dispatch";

            return true;
        }

        if (untrustedPrincipal && (grant.Capability == WorldCapability.Mutate) && (grant.Subject.Kind == GrantSubjectKind.Section) && (grant.KindMask is null)) {
            reason = $"an untrusted mutate grant to {grant.Principal.Describe()} over {grant.Subject.Describe()} requires an explicit verbs:<name,...> — an absent kind mask means FULL REACH at the admission door (a trusted principal's maskless row is the seeded default), so a maskless untrusted row would silently admit every kind {grant.Subject.Describe()} declares";

            return true;
        }

        // (0c-4) THE EVENT BUDGET — a SIBLING of Budget above, over the SAME row, metering a DIFFERENT cost (event
        //        push volume rather than query dispatch — see WorldGrant.EventBudget's own doc). Legal ONLY on
        //        Observe (events are an Observe-side disclosure); `events:0` is refused unconditionally, the
        //        identical "grant nothing instead" shape budget:0 already enforces; and it is REQUIRED on the three
        //        subject kinds that carry no OTHER live meaning under Observe — Screen (machine-memory watches),
        //        Region (enter/exit), and Seat (join/leave) — because a row over one of those with no event budget
        //        would be accepted-and-inert. It stays OPTIONAL on Body: an `observe body:<n>` row keeps its
        //        existing pose-query meaning unchanged whether or not it also carries an event budget.
        if (grant.EventBudget == 0) {
            reason = "events:0 is refused — a granted-but-never-pushed event row is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (grant.EventBudget is not null) {
            if (grant.Capability != WorldCapability.Observe) {
                reason = $"events:<n> is refused on {Label(capability: grant.Capability)} — only observe carries an event budget";

                return true;
            }

            if (!untrustedPrincipal) {
                reason = $"events:<n> is refused on {grant.Principal.Describe()}'s grant — trusted reads are unmetered";

                return true;
            }
        }

        if ((grant.Capability == WorldCapability.Observe) && (grant.EventBudget is null) &&
            (grant.Subject.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Region or GrantSubjectKind.Seat)) {
            reason = $"observe {grant.Subject.Describe()} requires an explicit events:<n> — this subject carries no query meaning, only events, so a row with no event budget would be accepted-and-inert";

            return true;
        }

        if (grant.HoldCeiling is { } holdCeiling) {
            if (grant.Capability != WorldCapability.Drive) {
                reason = $"hold:<seconds> is refused on {Label(capability: grant.Capability)} — only a drive grant governs timed channel presses";

                return true;
            }

            if ((holdCeiling < 0L) || (holdCeiling > Puck.Maths.FixedQ4816.FromDouble(value: WorldBody.MaxActionHoldSeconds).Value)) {
                reason = $"hold:<seconds> must be within [0, {WorldBody.MaxActionHoldSeconds:0.###}] — the authored hold ceiling exceeds the engine backstop";

                return true;
            }
        }

        // (0d) THE CO-DRIVING PAYLOAD (Reach/Consent/Ceiling) — the same parallel-field discipline the budget rules
        //      above apply, over the TWO DISTINCT things these fields carry (see WorldGrant's own doc). A contributor
        //      row says WHICH channels it may reach; the seat's own row says HOW FAR the pool may pull, one number
        //      per (seat, channel), authored by the seat and never derived across contributor rows:
        //      - Both fields only mean anything on a DRIVE row (the co-driving fold is a Drive-time concept); every
        //        other capability refuses them outright rather than silently carrying a field nothing reads.
        //      - `ceiling:0` and `channels:` naming nothing are refused UNCONDITIONALLY, the identical sentence shape
        //        as `budget:0`: pool-but-never-reach and reach-nothing are both accepted-and-inert — grant nothing.
        //      - A CEILING may only ride the OCCUPYING SEAT'S OWN row (seatN drive body:N) and must name the channels
        //        it applies to. A contributor row carrying one is refused by name: admitting it is exactly the
        //        derive-across-rows the design refuses, and it would let the contributor declare its own bound.
        //      - A REACH mask (no ceiling) may only ride an UNTRUSTED contributor's row. A trusted principal's
        //        contribution is added OUTSIDE the pool and is never masked (a human's own tool is not bounded by
        //        consent), so a mask there would be a field nothing reads.
        //      - The ceiling is range-pinned to raw `[1, One]` (0 is already refused above as `ceiling:0`) — the
        //        domain `Puck.Maths.FixedContributionFold.Evaluate`'s pool clamp reads `c` against directly (see its
        //        FLIP BOUND remarks for why a per-channel consent ceiling, not a fixed fraction, is what keeps an
        //        untrusted pool from flipping a binary channel's bit); anything outside it is refused by name.
        //        That bound, `c <= min(T - 1, One - T)`, is why CONSENT exists as a grant field at all: a pool
        //        exceeding EITHER arm can flip a human's bit against them, so it is precisely what a seat must
        //        author live. Extreme thresholds stay legal and are self-pricing — at `T = 1` or `T = One` the
        //        bound is 0, so every nonzero pool on that channel needs consent. The bound governs the UNTRUSTED
        //        half only; a trusted press is MEANT to be able to flip a bit.
        if (((grant.Reach is not null) || (grant.Consent is not null) || (grant.Ceiling is not null)) && (grant.Capability != WorldCapability.Drive)) {
            reason = $"channels/ceiling are refused on {Label(capability: grant.Capability)} — co-driving reach and the pooled ceiling only apply to a drive grant";

            return true;
        }

        if (grant.Ceiling == 0) {
            reason = "ceiling:0 is refused — a granted-but-never-reachable pool is accepted-and-inert; grant nothing instead";

            return true;
        }

        if ((grant.Reach is { } emptyReach) && emptyReach.IsEmpty) {
            reason = "channels:<none> is refused — a reach naming no channel is accepted-and-inert; grant nothing instead";

            return true;
        }

        if ((grant.Consent is { } emptyConsent) && emptyConsent.IsEmpty) {
            reason = "channels:<none> is refused — a consent gesture naming no channel is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (grant.Ceiling is { } ceiling) {
            if (!IsOwnSeatBody(principal: grant.Principal, subject: grant.Subject)) {
                reason = $"a pooled ceiling is authored by the occupying seat on its OWN body (seat<n> drive body:<n-1>), never on {grant.Principal.Describe()}'s row over {grant.Subject.Describe()} — the ceiling is one number per (seat, channel), never derived from contributor rows";

                return true;
            }

            if (grant.Consent is null) {
                reason = "a ceiling must name the channels it applies to (channels:<name,...>) — one number per (seat, channel), never a single scalar over the whole vector";

                return true;
            }

            if (grant.Reach is not null) {
                reason = "a pooled-ceiling gesture carries consent channels, never contributor reach";

                return true;
            }

            if ((ceiling < 0) || (ceiling > Puck.Maths.FixedQ4816.One.Value)) {
                reason = $"ceiling must be within [0, {Puck.Maths.FixedQ4816.One.Value}] raw (0..One) — {ceiling} is out of range";

                return true;
            }
        } else if (grant.Consent is not null) {
            reason = "consent channels must carry the positive ceiling they support";

            return true;
        } else if ((grant.Reach is not null) && (grant.Principal.Kind is PrincipalKind.Console or PrincipalKind.Seat)) {
            reason = $"a bare channel reach is refused on {grant.Principal.Describe()}'s row — a trusted contributor adds OUTSIDE the pool and is never masked; a seat's own row must name channels WITH a ceiling";

            return true;
        }

        // (0e) THE KIND MASK — the mutation dispatch door's payload, over MUTATION-KIND ordinals. Legal on exactly
        //      two row shapes (see CarriesKindMask): a Mutate row over a CONCRETE section (the addon mutation seam),
        //      and an Edit row over a CONCRETE state row (the bump-vs-redefine narrowing). Never the wildcard: a
        //      mask over "which kinds" presupposes ONE bounded target, and admitting one on the wildcard would let a
        //      trusted `mutate all`/`edit all` row narrow itself in a way no concrete grant could ever discover by
        //      reading its own row. A bit outside the target's OWN declared kind set
        //      (`WorldMutationKindCatalog.KindsOf`) is refused by name — an inert bit is a grant that lies, the
        //      identical posture IsLegitimateSubject's own remarks give for an illegitimate subject shape — and so
        //      is a mask whose bits, once bounded to that set, land on nothing at all (verbs:<none> is the co-driving
        //      channels:<none> refusal restated for this payload: pool-but-never-reach, grant nothing instead).
        if (grant.KindMask is { } kindMask) {
            if (!CarriesKindMask(capability: grant.Capability, subject: grant.Subject)) {
                reason = $"a verb mask is refused on {Label(capability: grant.Capability)} {grant.Subject.Describe()} — only a mutate grant over a concrete section:<name>, or an edit grant over a concrete state:<name>, carries one";

                return true;
            }

            var admissible = WorldMutationKindCatalog.KindsOf(section: ((grant.Subject.Kind == GrantSubjectKind.State) ? WorldSection.State : (WorldSection)grant.Subject.Value));
            var effective = kindMask.Meet(other: admissible);

            if (effective.Bits != kindMask.Bits) {
                reason = $"the verb mask names a mutation kind outside {grant.Subject.Describe()}'s own kind set ({admissible.Describe()}) — an inert bit is a grant that lies";

                return true;
            }

            if (effective.IsEmpty) {
                reason = "verbs:<none> is refused — a mask admitting no kind at all is accepted-and-inert; grant nothing instead";

                return true;
            }
        }

        // (0e-2) THE WRITE MASK — the CROSS-DOCUMENT durable-state channel's own payload, over
        //        WorldDocumentWriteKind operations rather than mutation kinds. Legal ONLY on a Mutate row over a
        //        concrete state subject (see CarriesWriteMask), which is the only door that speaks those operations
        //        (Server.WorldOwnedWorlds.Decide). A SEPARATE check from (0e), not a second branch inside it: the
        //        two vocabularies were one ulong once, and keeping the refusals apart is what keeps them apart.
        if (grant.WriteMask is { } writeMask) {
            if (!CarriesWriteMask(capability: grant.Capability, subject: grant.Subject)) {
                reason = $"a write mask is refused on {Label(capability: grant.Capability)} {grant.Subject.Describe()} — only a mutate grant over a concrete state:<name> reaches the cross-document durable-state channel that speaks Set/Add";

                return true;
            }

            var effectiveWrites = writeMask.Meet(other: DocumentWriteMask.All);

            if (effectiveWrites.Bits != writeMask.Bits) {
                reason = $"the write mask names an operation outside the declared set ({DocumentWriteMask.All.Describe()}) — an inert bit is a grant that lies";

                return true;
            }

            if (effectiveWrites.IsEmpty) {
                reason = "writes:<none> is refused — a mask admitting no operation at all is accepted-and-inert; grant nothing instead";

                return true;
            }
        }

        // (1) A DIFFERENT principal's exclusive reservation of an overlapping subject blocks EITHER order — an incoming
        //     exclusive collides with it, and an incoming ordinary grant would step onto a subject someone reserved.
        //     The ordinary `all` wildcard is EXEMPT here, symmetric to rule (2) below: acquisition never lets a
        //     LEGITIMATE exclusive concrete hold (rule (0b) already refused the illegitimate ones) block the
        //     permissive backdrop's wildcard, in EITHER direction. Without this exemption, an exclusive concrete hold
        //     taken AFTER the wildcard was narrowed (or that simply exists on some other subject when the wildcard is
        //     re-granted) permanently blackholes the wildcard's later re-grant — SubjectsOverlap treats the incoming
        //     `all` as overlapping every concrete subject of the capability, so ANY live exclusive reservation would
        //     otherwise reject it forever, with no revoke able to undo the block (the revoke targets the WILDCARD,
        //     which was never the thing reserved). That is exactly the asymmetry rule (0) already closes for an
        //     incoming exclusive `all`; this closes it for an incoming ORDINARY `all`. Enforcement (Allows) is what
        //     actually keeps the exclusive holder the sole effective owner of ITS OWN subject — acquisition refusing
        //     the wildcard's re-grant here would buy zero additional enforcement, only a permanent hole in the
        //     trusted principal's authority over every OTHER subject the wildcard reaches.
        if (!(!grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All))) {
            foreach (var pair in m_exclusive) {
                if ((pair.Key.Capability == grant.Capability) && (pair.Value != grant.Principal) && SubjectsOverlap(a: grant.Subject, b: pair.Key.Subject)) {
                    reason = $"{grant.Subject.Describe()} conflicts with {pair.Value.Describe()}'s exclusive {Label(capability: grant.Capability)} {pair.Key.Subject.Describe()}";

                    return true;
                }
            }
        }

        // (2) An incoming EXCLUSIVE grant additionally rejects when a DIFFERENT principal already holds the SAME concrete
        //     subject ordinarily (the ordinary-then-exclusive order). The wildcard `all` is exempt: a Contains of a
        //     concrete subject never matches an `all`-only set, so the seeded Drive/all backdrop does not block here.
        //     The SEEDED per-section Mutate rows are exempt the same way (they are the concrete spelling of the same
        //     permissive backdrop), so an exclusive section hold succeeds on a default table; a section row that has
        //     been REVOKED and then granted again blocks like any other hold, because Revoke is the only place the
        //     seed marker dies. Re-granting a row that was never revoked keeps it seeded, and that is deliberate: the
        //     re-grant changed nothing, and an operation with no observable effect should have no observable effect
        //     on seededness either.
        if (grant.Exclusive && (grant.Subject.Kind != GrantSubjectKind.All)) {
            foreach (var pair in m_byPrincipal) {
                if ((pair.Key != grant.Principal) &&
                    (pair.Value.For(capability: grant.Capability)?.Contains(item: grant.Subject) == true) &&
                    !m_seededSections.Contains(item: new SeededKey(Principal: pair.Key, Capability: grant.Capability, Subject: grant.Subject))) {
                    reason = $"{grant.Subject.Describe()} is already held by {pair.Key.Describe()}";

                    return true;
                }
            }
        }

        return false;
    }

    // The OCCUPYING SEAT'S OWN row — the one (principal, subject) pairing that may author a pooled ceiling, and the
    // one a Seat actor may administer at all. A local seat's binding to a body is fixed identity (slot n IS body
    // index n; see WorldPopulation.IsHumanOccupied), so "its own body" is exactly this equality and needs no
    // population read.
    private static bool IsOwnSeatBody(WorldPrincipal principal, GrantSubject subject) {
        return (principal.Kind == PrincipalKind.Seat) && (subject.Kind == GrantSubjectKind.Body) && (subject.Value == principal.Index);
    }

    // Two subjects overlap when they are identical or either is the `all` wildcard (which covers every concrete subject
    // of its capability). Used only for exclusive-reservation conflicts — the ordinary wildcard backdrop is exempt and
    // checked separately.
    //
    // THAT `||` IS THE WILDCARD EXPANSION, AND IT BELONGS IN THE CHECK, NEVER IN STORAGE. The two rules the grant
    // model states separately — attenuation is AND, and the wildcard is its own value never smeared to cover-all — do
    // not compose on their own: a holder carrying ONLY the wildcard, AND'ed against a request naming a concrete
    // subject, yields nothing and wrongly denies authority the principal genuinely holds. Expanding at the comparison
    // costs a two-term test; expanding into what is STORED destroys verdict distinction, revocation identity, and the
    // zero-slot projection. Any future set- or mask-shaped rewrite of this comparison inherits the same obligation.
    private static bool SubjectsOverlap(GrantSubject a, GrantSubject b) {
        return (a == b) || (a.Kind == GrantSubjectKind.All) || (b.Kind == GrantSubjectKind.All);
    }

    // The per-capability POSITIVE rule for which subject shapes a grant may legitimately name — written as what IS
    // admitted, not what is refused, so a NEW GrantSubjectKind or a NEW WorldCapability is refused BY DEFAULT AT THIS
    // GRANT DOOR rather than silently admitted through a deny-list some switch forgot to extend (the exhaustive
    // switch's `_ => false` arm is what does that for a future capability; a future subject kind falls out of every
    // arm's kind check the same way) — a policy decision, and "refused" here means Allows can never seat authority
    // for it. That is NOT the same guarantee PrincipalGrants' own storage switches (For/Set, below) make: a
    // capability that clears this door still needs a field to live in, and those switches are exhaustive over
    // WorldCapability's declared members and THROW on anything else, because a capability with no storage arm is a
    // programming error to fix by adding one, never a subject shape to accept or refuse. Two shapes recur across
    // every capability with a REAL enforced meaning: a CONCRETE subject in the
    // capability's own domain (a body for Drive — the one shape an addon's Drive handle table ever projects — a screen for Control,
    // a section for Mutate, a state row for Edit) is legitimate for ANY principal, because that is real, checkable
    // authority the moment it is granted; 'drive screen:0' or 'mutate body:3' is exactly as meaningless for a seat as
    // for an addon. The `all` WILDCARD is legitimate only for a principal already inside the local trust boundary FOR
    // THAT CAPABILITY: Console and every Seat generally (docs/capability-channels-plan.md's principal/adversary
    // table), plus a population Peer additionally for Control — "control any screen" is exactly the boot-seeded
    // engagement route every peer already carries (see the constructor). Letting an untrusted principal (an Addon, or
    // a Peer outside Control) hold a wildcard it was never seeded silently claims authority the seed never intended,
    // even where enforcement (Allows) alone would still deny most of what it reaches — the same seeded-permissive-
    // backdrop reasoning TryGrant's own remarks give for Drive/all applies identically to every other capability's
    // wildcard. A Drive or Observe Body is additionally bounded to an index the population actually holds
    // (WorldServer.Body's own (uint) bound), so a grant can never legitimately name a body that cannot exist. Observe
    // admits a concrete body for ANY principal because the read path is REAL now — an addon's pose query resolves an
    // Observe handle and is checked here (Server.WorldAddonRuntime's read point) — so an `observe body:<n>` row backs
    // live, checkable authority exactly the way a Drive row does.
    private bool IsLegitimateSubject(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        var trustedWildcard = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);

        return capability switch {
            WorldCapability.Drive => ((subject.Kind == GrantSubjectKind.Body) && ((uint)subject.Value < (uint)m_population)) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard),
            // Observe additionally admits Screen/Region/Seat, UNTRUSTED PRINCIPALS ONLY — the three event-only
            // subject kinds the world-events feed gates (see WorldGrant.EventBudget's own doc): a screen for
            // machine-memory watches, a named region for enter/exit, a local seat for join/leave. Only an addon
            // reads ABI Observation cells, so a trusted principal (Console/Seat) has no consumer for any of the
            // three — admitting them there would seat authority nothing enforces, the identical honesty rule this
            // whole method's remarks give for a legitimate-but-unenforceable shape. None of the three carries any
            // OTHER live meaning under Observe (no query verb reaches them), so a row naming one is inert unless it
            // also carries an EventBudget — enforced at the grant door, not here. Region is unbounded (no live
            // document access here to check the name against, and an unknown name simply never fires — see
            // GrantSubjectKind.Region's own doc); Seat is bounded to the reserved local-seat band.
            WorldCapability.Observe => ((subject.Kind == GrantSubjectKind.Body) && ((uint)subject.Value < (uint)m_population)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Screen)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Region)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Seat) && ((uint)subject.Value < (uint)WorldPopulationLimits.LocalSeatCount)) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard),
            WorldCapability.Control => (subject.Kind == GrantSubjectKind.Screen) ||
                // A route target may also be a BODY (context-routes widening) — a possession/co-drive route, bounded
                // by the population exactly like Drive/Observe's own body subjects.
                ((subject.Kind == GrantSubjectKind.Body) && ((uint)subject.Value < (uint)m_population)) ||
                ((subject.Kind == GrantSubjectKind.Composition) && trustedWildcard) ||
                ((subject.Kind == GrantSubjectKind.All) && (trustedWildcard || (principal.Kind == PrincipalKind.Peer))),
            WorldCapability.Mutate => (subject.Kind is GrantSubjectKind.Section or GrantSubjectKind.State) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard),
            WorldCapability.Edit => (subject.Kind == GrantSubjectKind.State) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard),
            _ => false,
        };
    }

    // WHICH ROW SHAPES CARRY WHICH MASK — stated positively and in ONE place, so the grant door (Conflicts), the
    // write/clear discipline (TryGrant), and any future consumer can never drift into disagreeing about it. A
    // capability/subject pair outside these two predicates carries NO mask, and a mask offered on one is refused by
    // name rather than silently stored where nothing reads it.
    //
    // The KIND mask (MutationKindMask, mutation-kind ordinals): a Mutate hold over a concrete document SECTION — the
    // addon mutation seam's dispatch door — or an Edit hold over a concrete STATE row, where it separates the
    // per-cell writes from the whole-row re-authoring beneath one subject.
    private static bool CarriesKindMask(WorldCapability capability, GrantSubject subject) =>
        ((capability == WorldCapability.Mutate) && (subject.Kind == GrantSubjectKind.Section)) ||
        ((capability == WorldCapability.Edit) && (subject.Kind == GrantSubjectKind.State));

    // The WRITE mask (DocumentWriteMask, WorldDocumentWriteKind operations): a Mutate hold over a concrete STATE
    // row — the cross-document durable-state write-back channel Server.WorldOwnedWorlds.Decide gates, the one door
    // whose vocabulary is Set/Add rather than mutation kinds.
    private static bool CarriesWriteMask(WorldCapability capability, GrantSubject subject) =>
        ((capability == WorldCapability.Mutate) && (subject.Kind == GrantSubjectKind.State));

    // The human-readable half of an illegitimate-subject rejection — names ONLY the shapes IsLegitimateSubject
    // actually admits for this (principal, capability), so the message can never claim a shape the rule does not
    // grant. Conflicts' `label` (built by the caller in WorldServer.Grant) already carries the rejected
    // principal/capability/subject, so this never repeats them.
    private string SubjectRule(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        // The ONE illegitimate Body case for the three body-domain capabilities is an out-of-range index (a Body
        // subject is otherwise always legitimate for them, for any principal) — name the actual population ceiling
        // rather than the generic body:<n> shape the operator already spelled correctly.
        if ((capability is WorldCapability.Drive or WorldCapability.Observe or WorldCapability.Control) && (subject.Kind == GrantSubjectKind.Body)) {
            return $"body:{subject.Value} does not exist — the population holds 0..{m_population - 1}";
        }

        // The ONE illegitimate Seat case (Observe only) is likewise an out-of-range index, not a wrong shape.
        if ((capability == WorldCapability.Observe) && (subject.Kind == GrantSubjectKind.Seat)) {
            return $"seat:{subject.Value} does not exist — local seats are 0..{WorldPopulationLimits.LocalSeatCount - 1}";
        }

        var trusted = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);

        return capability switch {
            WorldCapability.Drive => ((principal.Kind == PrincipalKind.Addon)
                ? "an addon must name the concrete body it drives (drive body:<n>)"
                : $"drive must name a concrete body (drive body:<n>){(trusted ? " or the wildcard 'all'" : "")}"),
            WorldCapability.Observe => (trusted
                ? "observe must name a concrete body (observe body:<n>) or the wildcard 'all' — screen/region/seat are event-only subjects with no trusted-principal consumer"
                : "observe must name a concrete body, screen, region, or seat (observe body:<n> | observe screen:<n> | observe region:<name> | observe seat:<n>)"),
            WorldCapability.Control => $"control must name a concrete screen or body (control screen:<n> | control body:<n>){((trusted || (principal.Kind == PrincipalKind.Peer)) ? " or the wildcard 'all'" : "")}",
            WorldCapability.Mutate => $"mutate must name a document section (mutate section:<name>){(trusted ? " or the wildcard 'all'" : "")}",
            WorldCapability.Edit => $"edit must name a concrete state row (edit state:<name>){(trusted ? " or the wildcard 'all'" : "")}",
            _ => "this capability accepts no subject today",
        };
    }

    private static string Label(WorldCapability capability) => capability.ToString().ToLowerInvariant();

    /// <summary>Removes a grant (capability+subject) from a principal, and clears any matching exclusive reservation.
    /// A no-op that returns <see langword="false"/> when the principal did not hold it. NOT part of
    /// <see cref="IWorldGrantsView"/>: this is an authority door, reached only through
    /// <see cref="WorldServer.Revoke"/>'s <see cref="HoldsForAdministration"/> actor check, never through the view a
    /// non-<see cref="WorldServer"/> caller holds.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to revoke.</param>
    /// <param name="subject">The subject to revoke.</param>
    /// <returns>Whether a grant was actually removed.</returns>
    public bool Revoke(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        var removed = false;
        var priorRoute = ((capability == WorldCapability.Control)
            ? (m_byPrincipal.TryGetValue(key: principal, value: out var priorGrants) ? priorGrants.RouteTarget() : null)
            : null);

        if (m_byPrincipal.TryGetValue(key: principal, value: out var grants)) {
            removed = grants.Remove(capability: capability, subject: subject);
        }

        var key = new ExclusiveKey(Capability: capability, Subject: subject);

        if (m_exclusive.TryGetValue(key: key, value: out var holder) && (holder == principal)) {
            _ = m_exclusive.Remove(key: key);
        }

        // The seed marker dies with the row: a re-grant after this revoke is a deliberate hold and blocks exclusive
        // acquisition like any other.
        _ = m_seededSections.Remove(item: new SeededKey(Principal: principal, Capability: capability, Subject: subject));

        // The fourth cleanup site (beside m_byPrincipal, m_exclusive, m_seededSections above): a revoked row's
        // dispatch budget dies with it, exactly like its exclusive reservation — a later re-grant that carries no
        // budget must not inherit one a prior, now-revoked hold left behind.
        _ = m_budgets.Remove(key: (principal, capability, subject));
        _ = m_eventBudgets.Remove(key: (principal, capability, subject));
        _ = m_holdCeilings.Remove(key: (principal, capability, subject));

        // The fifth and sixth cleanup sites: a revoked row's co-driving reach and its authored ceiling vector die with
        // it too — a later re-grant that carries no Reach/Consent/Ceiling must not inherit a prior, now-revoked reach or
        // pool. Revoking the seat's own Drive row is also the ONLY way to clear an authored ceiling: a ceiling gesture
        // writes the ordinals it names and leaves the rest alone, and `ceiling:0` is refused at the door.
        _ = m_channelReach.Remove(key: (principal, capability, subject));
        _ = m_poolCeilings.Remove(key: (principal, capability, subject));

        // The seventh cleanup site: a revoked row's masks die with it too, same as its budget/reach/ceiling —
        // both lanes, since a row can only ever legitimately carry one and clearing the other is free.
        _ = m_kindMasks.Remove(key: (principal, capability, subject));
        _ = m_writeMasks.Remove(key: (principal, capability, subject));

        if (removed) {
            // A handle designating this (capability, subject) must resolve to nothing on its very next use — see
            // WorldHandleTable's own remarks on why a cleared slot is what the projection BECOMES, never an
            // independent edit made here.
            m_revision++;

            if (capability == WorldCapability.Control) {
                var currentRoute = (m_byPrincipal.TryGetValue(key: principal, value: out var currentGrants) ? currentGrants.RouteTarget() : null);

                NotifyRouteTransition(principal: principal, previous: priorRoute, current: currentRoute);
            }
        }

        return removed;
    }

    /// <inheritdoc/>
    public WorldPrincipal? ExclusiveHolder(WorldCapability capability, GrantSubject subject) =>
        ExclusiveHolderOf(capability: capability, subject: subject);

    /// <inheritdoc/>
    public bool TryGetBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget) =>
        m_budgets.TryGetValue(key: (principal, capability, subject), value: out budget);

    /// <inheritdoc/>
    public bool TryGetEventBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget) =>
        m_eventBudgets.TryGetValue(key: (principal, capability, subject), value: out budget);

    /// <inheritdoc/>
    public long HoldCeiling(WorldPrincipal principal, GrantSubject subject) {
        var verdict = Allows(principal: principal, capability: WorldCapability.Drive, subject: subject);
        var decidingSubject = ((verdict.Rule == GrantRule.WildcardHold) ? GrantSubject.All : subject);

        return (m_holdCeilings.TryGetValue(key: (principal, WorldCapability.Drive, decidingSubject), value: out var ceiling)
            ? ceiling
            : s_defaultHoldCeiling);
    }

    /// <inheritdoc/>
    public bool TryGetChannelReach(WorldPrincipal principal, GrantSubject subject, out ChannelReachMask mask) =>
        m_channelReach.TryGetValue(key: (principal, WorldCapability.Drive, subject), value: out mask);

    /// <inheritdoc/>
    public ChannelCeilings PoolCeilings(WorldPrincipal seat, GrantSubject subject) =>
        (m_poolCeilings.TryGetValue(key: (seat, WorldCapability.Drive, subject), value: out var ceilings) ? ceilings : default);

    /// <inheritdoc/>
    public bool TryGetKindMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out MutationKindMask mask) =>
        m_kindMasks.TryGetValue(key: (principal, capability, subject), value: out mask);

    /// <inheritdoc/>
    public bool TryGetWriteMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out DocumentWriteMask mask) =>
        m_writeMasks.TryGetValue(key: (principal, capability, subject), value: out mask);

    /// <inheritdoc/>
    /// <remarks>Bumped by every mutator that changes a principal's held subject sets — <see cref="TryGrant"/>, a
    /// removing <see cref="Revoke"/>, and the engagement-route helpers below — never by a read.</remarks>
    public int Revision => m_revision;

    /// <inheritdoc/>
    /// <remarks>
    /// Sorted by (<see cref="GrantSubjectKind"/>, value, id) rather than returned in <see cref="HashSet{T}"/>
    /// enumeration order, which is a free-list/insertion-history artifact that is NOT stable across a rebuild ending
    /// at an identical subject set — a genuinely deterministic tie-break rather than "whatever order the BCL happens
    /// to hand back", which is reproducible run-to-run for a FIXED grant/revoke sequence but not stable across a
    /// DIFFERENT sequence that ends at the same subject set (docs/capability-channels-plan.md's "Authority is a
    /// handle, never a name").
    /// <para><b>Only a PER-INSTANCE subject kind is ever projected — the legal set is stated positively
    /// (<see cref="IsProjectable"/>), so a new kind is withheld by default.</b> A handle table's whole premise — "a
    /// guest still cannot name what it was not handed" — assumes every slot names ONE instance of the capability's
    /// domain; projecting a whole-domain designation would hand out a single index that designates everything the
    /// principal holds, and because low kinds sort first it would land at the most convenient index a naive guest asks
    /// for (docs/capability-channels-plan.md's Open Decision 5 — a population <see cref="PrincipalKind.Peer"/>'s
    /// seeded <see cref="WorldCapability.Control"/>/<see cref="GrantSubject.All"/> is exactly this case the moment a
    /// <c>Control</c> handle table exists). The rule was first written as its negation — "never
    /// <see cref="GrantSubjectKind.All"/>" — and an adversarial probe showed why a one-kind rejection list is the
    /// wrong shape: <see cref="GrantSubjectKind.Composition"/> is ALSO a whole-domain designation (its own doc: not a
    /// body, a screen, or a section) and sailed through the filter; the seeded seat rows projected it. The positive
    /// statement lives here rather than at each capability's <c>IsLegitimateSubject</c> door, because holding a
    /// whole-domain subject is legitimate, real authority for some (principal, capability) pairs today (a peer's
    /// boot-seeded <c>Control</c>/<c>all</c> route, a seat's <c>Control</c>/<c>composition</c>) — it is refused only
    /// from PROJECTION, never from the grant table itself.</para></remarks>
    public GrantSubject[] ProjectSubjects(WorldPrincipal principal, WorldCapability capability) {
        if (!m_byPrincipal.TryGetValue(key: principal, value: out var grants) || (grants.For(capability: capability) is not { } subjects)) {
            return [];
        }

        var projectableCount = 0;

        foreach (var subject in subjects) {
            if (IsProjectable(subject: subject)) {
                projectableCount++;
            }
        }

        var projected = new GrantSubject[projectableCount];
        var next = 0;

        foreach (var subject in subjects) {
            if (IsProjectable(subject: subject)) {
                projected[next++] = subject;
            }
        }

        Array.Sort(array: projected, comparison: CompareSubjects);

        return projected;
    }

    /// <summary>Determines whether ANY principal holds Drive CONCRETELY over <paramref name="body"/> — a POSSESSION, as
    /// distinct from the seeded Console <c>Drive/all</c> wildcard (which drives every body ambiently and answers
    /// <see cref="Allows"/> true for everything, but is not what "owned" means for a despawn guard). This type's own
    /// remarks state the backdrop precisely: "by default nothing drives an inhabited body ... except its own
    /// producer, and possessing one is an explicit <c>world.grant &lt;principal&gt; drive body:&lt;index&gt;</c>" —
    /// this is that explicit hold, read back. Linear over held principals: grant/revoke and rule-effect firing are
    /// both human/rule cadence (at most once per firing rule per tick), never the per-tick pose path, so a scan over
    /// the (small, human-populated) principal set is affordable here on the same terms <see cref="Conflicts"/>'s own
    /// exclusivity scans already are.</summary>
    /// <param name="body">The 0-based entity index to test.</param>
    /// <param name="holder">The possessing principal, when one exists.</param>
    /// <returns><see langword="true"/> when a concrete Drive hold exists.</returns>
    public bool IsBodyPossessed(int body, out WorldPrincipal holder) {
        var subject = GrantSubject.Body(index: body);

        foreach (var pair in m_byPrincipal) {
            if (pair.Value.For(capability: WorldCapability.Drive)?.Contains(item: subject) == true) {
                holder = pair.Key;

                return true;
            }
        }

        holder = default;

        return false;
    }

    // The POSITIVE statement of what a handle table may designate: exactly the kinds that name ONE instance of a
    // capability's domain — one body, one screen, one document section, one state row. Everything else is withheld by
    // default, which is the load-bearing property: All and Composition are whole-domain designations (and were the
    // live defect — All was the rejected case the first filter named, Composition the one it missed), and a future
    // kind starts unprojectable until someone decides it names one instance and adds it here.
    private static bool IsProjectable(GrantSubject subject) =>
        subject.Kind is GrantSubjectKind.Body or GrantSubjectKind.Screen or GrantSubjectKind.Section or GrantSubjectKind.State;

    // The total order ProjectSubjects sorts by — kind first (GrantSubjectKind's own declaration order), then value,
    // then id (the string-keyed kinds' only — State and Region — ordinal). Two subjects the table already treats as
    // equal (record-struct equality) sort equal here too; the order exists to be REPRODUCIBLE across a rebuild, not
    // to express any ranking of kinds.
    private static int CompareSubjects(GrantSubject a, GrantSubject b) {
        var kind = ((byte)a.Kind).CompareTo(value: (byte)b.Kind);

        if (kind != 0) {
            return kind;
        }

        var value = a.Value.CompareTo(value: b.Value);

        return ((value != 0) ? value : string.CompareOrdinal(strA: a.Id, strB: b.Id));
    }

    /// <inheritdoc/>
    public IReadOnlyList<(WorldCapability Capability, GrantSubject Subject)> Held(WorldPrincipal principal) {
        if (!m_byPrincipal.TryGetValue(key: principal, value: out var grants)) {
            return [];
        }

        var held = new List<(WorldCapability, GrantSubject)>();

        foreach (var capability in s_capabilityOrder) {
            if (grants.For(capability: capability) is not { Count: > 0 } subjects) {
                continue;
            }

            var projected = new GrantSubject[subjects.Count];

            subjects.CopyTo(array: projected);
            Array.Sort(array: projected, comparison: CompareSubjects);

            foreach (var subject in projected) {
                held.Add(item: (capability, subject));
            }
        }

        return held;
    }

    /// <summary>Snapshots the complete rows one peer principal currently holds, including every payload lane a peer
    /// may legally carry. Peer disconnect events carry this image so replay revokes the identical rows through the
    /// ordinary door.</summary>
    /// <param name="principal">The principal to snapshot.</param>
    /// <returns>The rows in stable capability/subject order.</returns>
    public IReadOnlyList<WorldGrant> Rows(WorldPrincipal principal) {
        var rows = new List<WorldGrant>();

        foreach (var (capability, subject) in Held(principal: principal)) {
            var key = (principal, capability, subject);
            var exclusive = m_exclusive.TryGetValue(key: new ExclusiveKey(Capability: capability, Subject: subject), value: out var holder) && (holder == principal);

            rows.Add(item: new WorldGrant(
                Principal: principal,
                Capability: capability,
                Subject: subject,
                Exclusive: exclusive,
                Budget: (m_budgets.TryGetValue(key: key, value: out var budget) ? budget : null),
                Reach: (m_channelReach.TryGetValue(key: key, value: out var reach) ? reach : null),
                KindMask: (m_kindMasks.TryGetValue(key: key, value: out var kinds) ? kinds : null),
                WriteMask: (m_writeMasks.TryGetValue(key: key, value: out var writes) ? writes : null),
                HoldCeiling: (m_holdCeilings.TryGetValue(key: key, value: out var holdCeiling) ? holdCeiling : null)
            ));
        }

        return rows;
    }

    /// <summary>Collects stale generations for one peer index. The caller revokes their rows through
    /// <see cref="WorldServer.Revoke"/>; this method never bypasses that door.</summary>
    /// <param name="index">The peer body index.</param>
    /// <param name="currentGeneration">The newly admitted generation.</param>
    /// <returns>The stale peer identities.</returns>
    public IReadOnlyList<WorldPrincipal> StalePeerGenerations(int index, int currentGeneration) {
        var stale = new List<WorldPrincipal>();

        foreach (var principal in m_byPrincipal.Keys) {
            if ((principal.Kind == PrincipalKind.Peer) && (principal.Index == index) && (principal.Generation != currentGeneration)) {
                stale.Add(item: principal);
            }
        }

        stale.Sort(comparison: static (left, right) => left.Generation.CompareTo(value: right.Generation));

        return stale;
    }

    // WorldCapability's own declaration order (Drive, Observe, Control, Mutate, Edit) — Held and Describe
    // enumerate in this order rather than Enum.GetValues (which happens to agree today, but naming the order
    // explicitly here means a future capability's declaration position can never silently reorder either report).
    private static readonly WorldCapability[] s_capabilityOrder = [
        WorldCapability.Drive, WorldCapability.Observe,
        WorldCapability.Control, WorldCapability.Mutate, WorldCapability.Edit,
    ];

    /// <inheritdoc/>
    public WorldHandleTable HandleTable(WorldPrincipal principal, WorldCapability capability) {
        var key = (Principal: principal, Capability: capability);

        if (!m_handleTables.TryGetValue(key: key, value: out var table)) {
            table = new WorldHandleTable(grants: this, principal: principal, capability: capability);
            m_handleTables[key] = table;
        }

        return table;
    }

    /// <inheritdoc/>
    public void SetControlRoute(WorldPrincipal principal, GrantSubject target, bool capture, ChannelReachMask channelMask) {
        ref var grants = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_byPrincipal, key: principal, exists: out _);
        var priorRoute = grants.RouteTarget();

        grants.ClearRoutes();
        grants.Add(capability: WorldCapability.Control, subject: target);
        grants.SetRoutePolicy(capture: capture, channelMask: channelMask);
        // The Control subject set just changed (a route added, any prior one cleared) — the same handle-table
        // staleness signal TryGrant/Revoke bump, since this writes the identical per-principal storage they do.
        m_revision++;
        NotifyRouteTransition(principal: principal, previous: priorRoute, current: grants.RouteTarget());
    }

    /// <inheritdoc/>
    public bool ClearControlRoute(WorldPrincipal principal) {
        var priorRoute = (m_byPrincipal.TryGetValue(key: principal, value: out var grants) ? grants.RouteTarget() : null);
        var cleared = (priorRoute is not null) && grants.ClearRoutes();

        if (cleared) {
            m_revision++;
            NotifyRouteTransition(principal: principal, previous: priorRoute, current: null);
        }

        return cleared;
    }

    private void NotifyRouteTransition(WorldPrincipal principal, GrantSubject? previous, GrantSubject? current) {
        if (previous == current) {
            return;
        }

        m_routeTransition(principal, previous, current);
    }

    /// <inheritdoc/>
    public GrantSubject? ControlRoute(WorldPrincipal principal) {
        return (m_byPrincipal.TryGetValue(key: principal, value: out var grants) ? grants.RouteTarget() : null);
    }

    /// <inheritdoc/>
    public bool RouteCapture(WorldPrincipal principal) {
        return (!m_byPrincipal.TryGetValue(key: principal, value: out var grants) || grants.RouteCapture());
    }

    /// <inheritdoc/>
    public ChannelReachMask RouteChannelMask(WorldPrincipal principal) {
        return (m_byPrincipal.TryGetValue(key: principal, value: out var grants) ? grants.RouteChannelMask() : ChannelReachMask.All);
    }

    /// <inheritdoc/>
    public void CollectRouteHolders(GrantSubject target, List<WorldPrincipal> into) {
        into.Clear();

        foreach (var pair in m_byPrincipal) {
            if (pair.Value.HoldsRoute(subject: target)) {
                into.Add(item: pair.Key);
            }
        }
    }

    /// <inheritdoc/>
    public string Describe(WorldPrincipal? filter) {
        var builder = new StringBuilder(value: "[world.grants:");
        var any = false;

        foreach (var pair in m_byPrincipal) {
            if ((filter is { } only) && (pair.Key != only)) {
                continue;
            }

            var held = Held(principal: pair.Key);

            if (held.Count == 0) {
                continue;
            }

            _ = builder.Append(value: any ? " | " : " ").Append(value: pair.Key.Describe()).Append(value: ' ');

            for (var index = 0; (index < held.Count); index++) {
                var (capability, subject) = held[index];
                var isExclusive = m_exclusive.TryGetValue(key: new ExclusiveKey(Capability: capability, Subject: subject), value: out var holder) && (holder == pair.Key);

                if (index > 0) {
                    _ = builder.Append(value: ' ');
                }

                _ = builder.Append(value: capability.ToString().ToLowerInvariant()).Append(value: '/').Append(value: subject.Describe());

                if (isExclusive) {
                    _ = builder.Append(value: "(x)");
                }

                if (m_budgets.TryGetValue(key: (pair.Key, capability, subject), value: out var budget)) {
                    _ = builder.Append(value: " budget:").Append(value: budget);
                }

                if (m_eventBudgets.TryGetValue(key: (pair.Key, capability, subject), value: out var eventBudget)) {
                    _ = builder.Append(value: " events:").Append(value: eventBudget);
                }

                if (capability == WorldCapability.Drive) {
                    var holdCeiling = (m_holdCeilings.TryGetValue(key: (pair.Key, capability, subject), value: out var authoredHold)
                        ? authoredHold
                        : s_defaultHoldCeiling);
                    _ = builder.Append(value: " hold:").Append(value: ((double)Puck.Maths.FixedQ4816.FromRawBits(value: holdCeiling)).ToString(format: "0.###", provider: CultureInfo.InvariantCulture));
                }

                if (m_channelReach.TryGetValue(key: (pair.Key, capability, subject), value: out var reach)) {
                    _ = builder.Append(value: " channels:0x").Append(value: reach.Bits.ToString(format: "x"));
                }

                // NAMES, never a hex lane: a read-back an operator cannot decode by eye is a read-back that does
                // not close the loop the authoring token opened (world.grant takes verbs:<Name,...>/writes:<Name,...>).
                if (m_kindMasks.TryGetValue(key: (pair.Key, capability, subject), value: out var kindMask)) {
                    _ = builder.Append(value: " verbs:").Append(value: kindMask.Describe());
                }

                if (m_writeMasks.TryGetValue(key: (pair.Key, capability, subject), value: out var writeMask)) {
                    _ = builder.Append(value: " writes:").Append(value: writeMask.Describe());
                }

                // The seat's authored ceilings render per ORDINAL, never as one scalar: a row carrying a `forward`
                // ceiling and a different `turn` ceiling has to read as the two numbers it is.
                if (m_poolCeilings.TryGetValue(key: (pair.Key, capability, subject), value: out var ceilings)) {
                    var first = true;

                    for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                        if (ceilings[ordinal] == 0L) {
                            continue;
                        }

                        _ = builder.Append(value: (first ? " ceilings:" : ",")).Append(value: ordinal).Append(value: '=').Append(value: ceilings[ordinal]);
                        first = false;
                    }
                }
            }

            any = true;
        }

        if (!any) {
            _ = builder.Append(value: " (none)");
        }

        return builder.Append(value: ']').ToString();
    }

    // One principal's five per-capability subject sets, allocated lazily. A struct held by ref in the dictionary; the
    // sets are reference types so ref-mutation persists.
    private struct PrincipalGrants {
        private HashSet<GrantSubject>? m_drive;
        private HashSet<GrantSubject>? m_observe;
        private HashSet<GrantSubject>? m_control;
        private HashSet<GrantSubject>? m_mutate;
        private HashSet<GrantSubject>? m_edit;
        // The route's OWN policy payload — capture and channel mask — set together with the route subject by
        // SetControlRoute, alongside the m_control set itself rather than through TryGrant's general per-row payload
        // lanes (m_budgets/m_channelReach/etc.), because a route is single-per-principal and these two values ride
        // WITH it, never independently. Stale once the route is cleared (RouteTarget() returning null gates their
        // relevance — nothing reads them without a live route to ask about first).
        //
        // STORED INVERTED (m_routeMirror, true means capture:false) so a route NEVER established through
        // SetControlRoute — a bare `world.grant … control screen:N`/`control body:N` with no matching Engage call —
        // reads its default bool zero-value as CAPTURED (RouteCapture() true), matching the historical assumption
        // every existing route made. This is the exact discriminator WorldEngagement.ResolveDisengage needs: an
        // established capture:false (mirror) route's disengage is an ORDINARY success, never the
        // route-without-latch REPAIR case that assumption exists to catch.
        private bool m_routeMirror;
        private ChannelReachMask m_routeChannelMask;

        // Exhaustive over WorldCapability's five declared members ONLY — a future member has no storage field to fall
        // back to, so it throws rather than silently sharing m_edit's slot (see IsLegitimateSubject's remarks for why
        // "refused by default" means something different here than at the grant door: this is a programming error to
        // fix by adding a field and an arm, not a policy decision about what to admit).
        // HONEST REACH, per adversarial probe: this tripwire is defense-in-depth, not a gate anything currently
        // reaches — every data path (document grants, world.grant tokens, addon manifests) is filtered by
        // IsLegitimateSubject or a closed parse before storage is consulted, and Allows itself short-circuits on the
        // m_byPrincipal miss first, so an unknown capability queried against an UNSEATED principal returns false while
        // the same query against a seated one throws. The throw exists for the caller that bypasses those doors from
        // inside the assembly; do not credit it with guarding the document path.
        public readonly HashSet<GrantSubject>? For(WorldCapability capability) => capability switch {
            WorldCapability.Drive => m_drive,
            WorldCapability.Observe => m_observe,
            WorldCapability.Control => m_control,
            WorldCapability.Mutate => m_mutate,
            WorldCapability.Edit => m_edit,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(capability), actualValue: capability, message: $"WorldCapability.{capability} has no storage arm in PrincipalGrants.For — add a field and a case here before granting it."),
        };

        public void Add(WorldCapability capability, GrantSubject subject) {
            _ = Set(capability: capability).Add(item: subject);
        }

        public readonly bool Remove(WorldCapability capability, GrantSubject subject) {
            return (For(capability: capability)?.Remove(item: subject) ?? false);
        }

        // Whether the Control set holds EXACTLY this subject — used both for the ordinary membership test and for
        // CollectRouteHolders' route-holder scan, which now queries a screen OR a body target identically.
        public readonly bool HoldsRoute(GrantSubject subject) {
            return (m_control?.Contains(item: subject) ?? false);
        }

        // The one route a principal holds, if any — a Control subject that is a REAL route target (Screen or Body),
        // never the wildcard/composition rows the same set also carries.
        public readonly GrantSubject? RouteTarget() {
            if (m_control is { } control) {
                foreach (var subject in control) {
                    if (subject.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Body) {
                        return subject;
                    }
                }
            }

            return null;
        }

        public readonly bool ClearRoutes() {
            if (m_control is not { } control) {
                return false;
            }

            var removed = control.RemoveWhere(match: static subject => (subject.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Body));

            return (removed > 0);
        }

        // Writes the route's capture/mask payload alongside its subject — called only from SetControlRoute, in the
        // same breath as the route subject itself.
        public void SetRoutePolicy(bool capture, ChannelReachMask channelMask) {
            m_routeMirror = !capture;
            m_routeChannelMask = channelMask;
        }

        // Defaults to captured/all when no route is held, or when a route was never established through
        // SetControlRoute (see m_routeMirror's remarks) — RouteCapture/RouteChannelMask's own callers already treat
        // "no route" as the all-permissive baseline (see IWorldGrantsView's remarks), so this never needs its own
        // null-route branch.
        public readonly bool RouteCapture() => !m_routeMirror;
        public readonly ChannelReachMask RouteChannelMask() => ((m_routeChannelMask.Bits == 0UL) ? ChannelReachMask.All : m_routeChannelMask);

        // Exhaustive over WorldCapability's five declared members ONLY, mirroring For's own arms exactly — see its
        // comment for why the fallthrough throws instead of defaulting to m_edit.
        private HashSet<GrantSubject> Set(WorldCapability capability) {
            switch (capability) {
                case WorldCapability.Drive:
                    return (m_drive ??= new HashSet<GrantSubject>());
                case WorldCapability.Observe:
                    return (m_observe ??= new HashSet<GrantSubject>());
                case WorldCapability.Control:
                    return (m_control ??= new HashSet<GrantSubject>());
                case WorldCapability.Mutate:
                    return (m_mutate ??= new HashSet<GrantSubject>());
                case WorldCapability.Edit:
                    return (m_edit ??= new HashSet<GrantSubject>());
                default:
                    throw new ArgumentOutOfRangeException(paramName: nameof(capability), actualValue: capability, message: $"WorldCapability.{capability} has no storage arm in PrincipalGrants.Set — add a field and a case here before granting it.");
            }
        }
    }

    // The reverse-index key for the exclusive-holder table.
    private readonly record struct ExclusiveKey(WorldCapability Capability, GrantSubject Subject);

    // The seed-marker key: one permissive-default row as constructed (principal + capability + subject).
    private readonly record struct SeededKey(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject);
}
