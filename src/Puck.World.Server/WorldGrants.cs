using System.Globalization;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The server's one capability table — the single primitive that engagement, machine-input ownership, and addon
/// slot ownership all reduce to: a set of <c>(principal, capability, subject)</c>
/// grants, seeded permissive for local play and mutated live through <c>world.grant</c>/<c>world.revoke</c>. Every
/// write boundary asks <see cref="Allows"/> before it acts; <c>Puck.World.WorldEngagement</c> is a view over the
/// <see cref="WorldCapability.Control"/> screen routes here, not a parallel table.
/// </summary>
/// <remarks>
/// <para>Storage is a per-principal record of five subject sets (one per capability), so a per-tick
/// <see cref="Allows"/> is a dictionary lookup plus one <see cref="HashSet{T}"/> membership test — allocation-free and
/// O(1). A grant matches when its subject set holds the queried subject or the <see cref="GrantSubject.All"/> wildcard.</para>
/// <para><b>Exclusivity — the chosen semantic (an exclusive hold is enforced, not just reserved).</b> Exclusivity is a
/// reservation over one concrete <c>(capability, subject)</c>, tracked in a reverse index. An exclusive grant must name
/// a concrete subject: an exclusive <see cref="GrantSubject.All"/> ("exclusively own everything") is rejected at
/// acquisition in every order and on a fresh table — it has no legitimate consumer and would otherwise be
/// order-dependently dishonest (accepted one way, then denying every concrete seat at enforcement; rejected the other).
/// A concrete exclusive hold is honored in two places that together give the invariant "an exclusively-held body has
/// exactly one effective driver":</para>
/// <list type="bullet">
/// <item><description><b>Acquisition (<see cref="TryGrant"/>).</b> An incoming grant — exclusive or ordinary — is
/// rejected when it would put a different principal alongside an existing conflicting hold: (1) any exclusive
/// reservation of an overlapping subject blocks it in either order (exclusive-then-ordinary and ordinary-then-exclusive
/// both reject), and (2) an incoming exclusive additionally rejects when a different principal already holds the same
/// concrete subject ordinarily. An incoming exclusive <see cref="GrantSubject.All"/> is rejected outright (above). The
/// wildcard <see cref="GrantSubject.All"/> ordinary grant is deliberately exempt from rule (1) in both directions —
/// an existing exclusive concrete hold never blocks a later ordinary wildcard re-grant, and the seeded wildcard never
/// blocks a later exclusive concrete acquisition: the permissive local defaults seed the console with <c>Drive/all</c>
/// and seats with <c>Control/all</c>; an admitted peer generation receives that row from its
/// <c>PeerAdmitted</c> server event
/// (not <c>Drive</c>) — so by default nothing drives an inhabited body except its own producer, and possessing
/// one is an explicit <c>world.grant &lt;principal&gt; drive body:&lt;index&gt;</c>. That is the correct default and it
/// comes for free from the existing seed; no new grant subject or capability is added for inhabitation.
/// This backdrop must never block a principal (e.g. an addon) from taking an exclusive hold on one specific body —
/// so <c>world.grant addon:x drive body:n exclusive</c> succeeds even though the console holds <c>Drive/all</c> — and
/// the exclusive hold must never permanently prevent the wildcard's later re-grant either: narrowing
/// (<c>world.revoke console drive all</c>) then re-widening (<c>world.grant console drive all</c>) succeeds no
/// matter what exclusive concrete holds exist elsewhere, because enforcement (<see cref="Allows"/>) — not
/// acquisition-time blocking — is what keeps an exclusive holder the sole effective owner of its own subject. The
/// seeded per-section <c>Mutate</c> defaults get the same exemption: they are the concrete spelling of the same
/// permissive backdrop (per-section only so one is revocable), so
/// <c>world.grant seat1 mutate section:screens exclusive</c> succeeds on a default table — the seed must never block
/// an exclusive editing hold. A section row deliberately granted after boot (or re-granted after a revoke) is a real
/// hold and blocks like any other; only the untouched seed is exempt.</description></item>
/// <item><description><b>Enforcement (<see cref="Allows"/>).</b> Once <c>body:n</c> is exclusively reserved by principal
/// P, <see cref="Allows"/> answers true only for P — the exclusive holder overrides every other grant, including the
/// permissive <c>Drive/all</c> wildcard. So the exempt backdrop from acquisition cannot actually drive an exclusively
/// held body: exclusivity, not acquisition-time blocking, is what makes the reservation exclusive at the intent
/// boundary. When a subject is not exclusively reserved, the normal wildcard/subject-set logic applies unchanged.</description></item>
/// </list>
/// <para>This table owns no lock. Ordinary grants apply and read on the launcher tick thread; authenticated
/// federation operations and the transfer host reach it only through <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>,
/// which serializes those narrow operations against the fixed-step fold.</para>
/// </remarks>
public sealed class WorldGrants : IWorldGrantsView {
    private static readonly long DefaultHoldCeiling = Puck.Maths.FixedQ4816.FromDouble(value: WorldGrant.DefaultHoldSeconds).Value;
    // The per-body-index own-body default sets, minted on first read and never mutated afterward — every
    // uncomposed participant shares its index's instance.
    private static readonly IReadOnlyList<ControlApplication>?[] s_ownBodyApplications = new IReadOnlyList<ControlApplication>?[WorldPopulationLimits.CapacityCeiling];
    private readonly Dictionary<WorldPrincipal, PrincipalGrants> m_byPrincipal = new();
    // Every principal that has COMPOSED an application set away from its own-body default. An absent row IS the
    // default (see DefaultApplications) — the single storage engagement lives in, distinct from the capability sets
    // above: a Control grant authorizes composing, it never mints an application, so a route and a latch can no
    // longer disagree.
    private readonly Dictionary<WorldPrincipal, List<ControlApplication>> m_applications = new();
    // (capability, subject) -> the exclusive holder. Only exclusive grants appear here.
    private readonly Dictionary<ExclusiveKey, WorldPrincipal> m_exclusive = new();
    // (principal, capability, subject) -> the row's per-tick dispatch budget. Written by TryGrant on every accepted
    // grant that carries one (last-write-wins), cleared by Revoke. Only an untrusted principal's (Addon/Peer)
    // metered row — Observe, Drive, or Mutate over a concrete section:<name> — ever has an entry here.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ushort> m_budgets = new();
    // (principal, capability, subject) -> the row's per-tick event-push budget, independent of m_budgets: Budget
    // meters query dispatch, this meters event push volume. Written by TryGrant, cleared by Revoke.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ushort> m_eventBudgets = new();
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), long> m_holdCeilings = new();
    // Co-driving payload, split by writer: m_channelReach keys an untrusted contributor's Drive row to which channel
    // ordinals it may touch; m_poolCeilings keys the OCCUPYING SEAT'S OWN Drive row (seatN drive body:N) to one
    // ceiling per channel ordinal, bounding how far the pool may pull that channel. A ceiling is never derived from
    // contributor rows. Both are written unconditionally by TryGrant on every accepted grant that carries one
    // (last-write-wins) and cleared by Revoke.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ChannelReachMask> m_channelReach = new();
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), ChannelCeilings> m_poolCeilings = new();
    // (principal, capability, subject) -> the mutation-kind ordinals a Mutate-over-section or Edit-over-state row
    // may dispatch. A null mask on a re-grant of the same row CLEARS the entry, rather than leaving it untouched.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), MutationKindMask> m_kindMasks = new();
    // (principal, capability, subject) -> the WorldDocumentWriteKind operations a Mutate-over-state row may perform
    // on the cross-document durable-state channel. Separate table from m_kindMasks; same write/clear discipline.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject), DocumentWriteMask> m_writeMasks = new();
    // The seeded per-section Mutate backdrop rows (principal + section subject), recorded at construction so an
    // exclusive acquisition never blocks on them. Revoke deletes the marker; a later re-grant is a deliberate hold.
    private readonly HashSet<SeededKey> m_seededSections = new();
    // (principal, capability) -> the lazily-built handle-table cache; a WorldHandleTable's own staleness check
    // (keyed off m_revision below) decides whether it re-projects, not this cache. Only Addon/Peer principals ever
    // get an entry — WorldHandleTable's constructor refuses Console/Seat.
    private readonly Dictionary<(WorldPrincipal Principal, WorldCapability Capability), WorldHandleTable> m_handleTables = new();
    // Group+membership+ownership indices, resynced wholesale from the live document's `groups` section on every
    // WorldServer.Install — never incrementally patched, so Allows always reads this tick's settled document.
    //
    // m_groupMembership: principal -> the group ids it currently belongs to. Consulted by Allows' group-expansion
    // step. Flat only — never itself keyed by a group principal on the value side.
    private readonly Dictionary<WorldPrincipal, List<string>> m_groupMembership = new();
    // m_groupReach: group id -> the capabilities at least one of its kind's declared roles reaches. Consulted by
    // TryGrant's reachability check so a group grant that no role could ever exercise is refused.
    private readonly Dictionary<string, HashSet<WorldCapability>> m_groupReach = new();
    // m_ownedGroups: principal -> the group ids it currently owns, resynced in the same pass. Consulted by Allows'
    // ownership-expansion fallback. A group-owns-group row resolves one level at sync time against the owning
    // group's current roster — flat, never recursive.
    private readonly Dictionary<WorldPrincipal, List<string>> m_ownedGroups = new();
    // m_driveGates: 0-based body entity index -> the name of the first-in-document-order WorldStateRow declaring
    // GatesDrive whose per-body cell currently reads nonzero. Resynced wholesale by SyncState, alongside SyncGroups.
    // Consulted by WorldServer.ApplyIntentSubmission and world.why, never folded into Allows: a drive gate SUBTRACTS
    // reach (unlike ownership, which only adds), so it must gate at the ingress, never inside Allows, or it would
    // corrupt every capability query.
    private readonly Dictionary<int, string> m_driveGates = new();
    // WorldCapability's own declaration order (Drive, Observe, Control, Mutate, Edit) — Held and Describe
    // enumerate in this order rather than Enum.GetValues (which happens to agree today, but naming the order
    // explicitly here means a future capability's declaration position can never silently reorder either report).
    private static readonly WorldCapability[] CapabilityOrder = [
        WorldCapability.Drive, WorldCapability.Observe,
        WorldCapability.Control, WorldCapability.Mutate, WorldCapability.Edit,
    ];

    // The entity-table ceiling passed at construction — the same value WorldServer.Body(int) bounds against — so a
    // Drive grant can never legitimately name a body index the population does not actually hold (see
    // IsLegitimateSubject).
    private readonly int m_population;
    private readonly Action<WorldPrincipal, GrantSubject?, GrantSubject?> m_routeTransition;

    // Bumped on every change to a principal's held subject sets — TryGrant, a removing Revoke, and the
    // engagement-route helpers below. A WorldHandleTable compares this against the revision it last rebuilt from.
    private int m_revision;

    /// <summary>Seeds the permissive local-play defaults so boot behavior is unchanged until someone revokes: every seat
    /// holds Drive over its own body only — never the wildcard (see <see cref="IsLegitimateSubject"/>'s remarks for why
    /// a Seat may still be granted Drive/all later despite not being seeded with it) — plus
    /// Observe/Control/Mutate/Edit over its domain; the console holds Drive over any body (the table's only
    /// boot-seeded Drive/all) plus Observe/Control/Mutate/Edit over its domain. Each concrete peer generation receives
    /// only Control over any screen when its <c>PeerAdmitted</c> event applies (population entries engage diegetic
    /// machines today, exactly like seats — the route capability, not Drive: peers do not submit intents, and get no
    /// Observe/Mutate/Edit at all). Addons get
    /// nothing — Observe is a newer capability and this posture extends the same deny-by-default rule to
    /// it: Console is fully trusted (grants are honesty, not security) and Seats are trusted locally (seeded
    /// permissive so local play is never gated on this until someone narrows trust); Addon and Peer are the untrusted
    /// side and get neither verb at seed. Mutate is seeded per-section (not the wildcard) so a single section can be
    /// revoked; Observe, Control, and Edit use the wildcard for every principal <see cref="SeedDomain"/> seeds
    /// (Console and every Seat); Drive is the one exception — per-body for a Seat, the wildcard only for
    /// Console.</summary>
    /// <param name="seatCount">The reserved local-seat count (each seat 0..seatCount-1 gets its default body grant).</param>
    /// <param name="population">The entity-table ceiling used to validate concrete body subjects.</param>
    /// <param name="routeTransition">The observer called after a principal's effective Control route changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routeTransition"/> is <see langword="null"/>.</exception>
    public WorldGrants(int seatCount, int population, Action<WorldPrincipal, GrantSubject?, GrantSubject?> routeTransition) {
        ArgumentNullException.ThrowIfNull(argument: routeTransition);

        m_population = population;
        m_routeTransition = routeTransition;

        for (var slot = 0; (slot < seatCount); slot++) {
            var seat = WorldPrincipal.Seat(slot: slot);

            _ = TryGrant(
                grant: new WorldGrant(
                    Principal: seat,
                    Capability: WorldCapability.Drive,
                    Subject: GrantSubject.Body(index: slot),
                    Exclusive: false
                ),
                reason: out _
            );
            SeedDomain(principal: seat);
        }

        SeedDomain(principal: WorldPrincipal.Console);
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: WorldPrincipal.Console,
                Capability: WorldCapability.Drive,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            reason: out _
        );

        // Peer authority is minted only when a concrete generation is admitted. Pre-seeding index-only peers here
        // would let a later occupant inherit an earlier session's authority.
    }

    private void AddOwnedGroup(WorldPrincipal owner, string groupId) {
        if (!m_ownedGroups.TryGetValue(
            key: owner,
            value: out var ownedOf
        )) {
            ownedOf = new List<string>();
            m_ownedGroups[owner] = ownedOf;
        }

        ownedOf.Add(item: groupId);
    }
    // Which row shapes carry which mask, stated positively in one place so Conflicts, TryGrant, and any future
    // consumer never drift into disagreeing about it. A pair outside these two predicates carries no mask.
    //
    // The KIND mask (MutationKindMask): a Mutate hold over a concrete section or one of its row-scoped subjects, or
    // an Edit hold over a concrete state row, where it separates per-cell writes from whole-row re-authoring.
    private static bool CarriesKindMask(WorldCapability capability, GrantSubject subject) =>
        (((capability == WorldCapability.Mutate) && IsMutateDispatchSubject(subject: subject)) ||
        ((capability == WorldCapability.Edit) && (subject.Kind == GrantSubjectKind.State)));
    // The Mutate subjects that pass through a DISPATCH door — a whole section, or one row of one. They are the rows
    // that carry a verb mask and a dispatch budget, and the ones TryAdmitMutation's first gate consults. A
    // Mutate/state:<name> row is deliberately absent: that is the cross-document write-back channel, which speaks
    // DocumentWriteMask and has no dispatch door to meter.
    private static bool IsMutateDispatchSubject(GrantSubject subject) =>
        (subject.Kind is GrantSubjectKind.Section or GrantSubjectKind.Creation or GrantSubjectKind.Placement);
    // The section whose declared mutation-kind set bounds a maskable subject's verb mask — the subject's own section
    // for a Section hold, the owning section for a row-scoped one, and WorldSection.State for an Edit/state row.
    private static WorldSection MaskSectionOf(GrantSubject subject) => subject.Kind switch {
        GrantSubjectKind.Creation => WorldSection.Creations,
        GrantSubjectKind.Placement => WorldSection.Placements,
        GrantSubjectKind.State => WorldSection.State,
        _ => ((WorldSection)subject.Value),
    };
    // The WRITE mask (DocumentWriteMask, WorldDocumentWriteKind operations): a Mutate hold over a concrete STATE
    // row — the cross-document durable-state write-back channel Server.WorldOwnedWorlds.Decide gates, the one door
    // whose vocabulary is Set/Add rather than mutation kinds.
    private static bool CarriesWriteMask(WorldCapability capability, GrantSubject subject) =>
        ((capability == WorldCapability.Mutate) && (subject.Kind == GrantSubjectKind.State));
    // The total order ProjectSubjects sorts by — kind first (GrantSubjectKind's own declaration order), then value,
    // then id (the string-keyed kinds' only — State and Region — ordinal). Two subjects the table already treats as
    // equal (record-struct equality) sort equal here too; the order exists to be REPRODUCIBLE across a rebuild, not
    // to express any ranking of kinds.
    private static int CompareSubjects(GrantSubject a, GrantSubject b) {
        var kind = ((byte)a.Kind).CompareTo(value: ((byte)b.Kind));

        if (kind != 0) {
            return kind;
        }

        var value = a.Value.CompareTo(value: b.Value);

        return ((value != 0)
            ? value
            : string.CompareOrdinal(
                strA: a.Id,
                strB: b.Id
            )
        );
    }
    // Whether an incoming grant conflicts with an existing hold under the exclusivity rule. Grant/revoke is a
    // human-cadence op (never the tick path), so the two scans are affordable; both are skipped entirely for the common
    // idempotent re-grant (a matching holder is the incoming principal itself).
    private bool Conflicts(WorldGrant grant, out string reason) {
        reason = string.Empty;

        // The world's own authority is structural — TryAdmitMutation admits it before this table is consulted — so
        // a row naming it would be an inert phantom grant. Refused here so an author learns why.
        if (grant.Principal.Kind == PrincipalKind.World) {
            reason = "the world's own authored program holds no grants — its authority is structural (a rule's effects and a kit's generate effect are the document acting on itself, never an actor submitting); a row here would be accepted and inert";

            return true;
        }

        // A document holds no LIVE rows either — its grants are read off the owner's document
        // (Server.WorldOwnedWorlds.Decide/TryReadDurableState consult definition.Grants directly), never off this
        // table; a live row here would be budget-less, mask-less, and consulted by nothing.
        // `world.grants document:<id>` echoes the document-authored rows instead.
        if (grant.Principal.Kind == PrincipalKind.Document) {
            reason = "a document holds no LIVE grants — the cross-document durable-state write-back channel reads its rows off the OWNER'S DOCUMENT (world.grant.set authors them, world.grants document:<id> echoes them), so a row here would be accepted and inert";

            return true;
        }

        // An exclusive reservation must name a concrete subject — an exclusive `all` would slip past acquisition
        // and then deny every concrete seat at enforcement, so it is rejected outright.
        if (
            grant.Exclusive &&
            (grant.Subject.Kind == GrantSubjectKind.All)
        ) {
            reason = $"an exclusive {Label(capability: grant.Capability)} reservation must name a concrete subject (exclusive 'all' is not allowed)";

            return true;
        }

        // Every capability accepts only the subject shapes IsLegitimateSubject admits (a positive rule, so a future
        // subject kind or capability is refused by default). An illegitimate grant (a Drive over a screen, a
        // Mutate over a body...) would resolve to no enforceable authority yet still render as a held row — refused
        // here rather than silently seating a principal that holds nothing enforceable.
        if (!IsLegitimateSubject(
            principal: grant.Principal,
            capability: grant.Capability,
            subject: grant.Subject
        )) {
            reason = SubjectRule(
                principal: grant.Principal,
                capability: grant.Capability,
                subject: grant.Subject
            );

            return true;
        }

        // A grant naming a GROUP principal must not accept authority no role of its kind could ever exercise.
        // m_groupReach is resynced wholesale on every document swap, so this reads the current declared kind.
        if (
            (grant.Principal.Kind == PrincipalKind.Group) &&
            (!m_groupReach.TryGetValue(
            key: (grant.Principal.Name ?? string.Empty),
            value: out var reach
        ) || !reach.Contains(item: grant.Capability))
        ) {
            reason = (m_groupReach.ContainsKey(key: (grant.Principal.Name ?? string.Empty))
                ? $"{grant.Principal.Describe()}'s kind declares no role reaching {Label(capability: grant.Capability)} — granting it would be accepted-and-inert; widen the kind's roles instead"
                : $"{grant.Principal.Describe()} does not exist — form it first, or author its kind"
            );

            return true;
        }

        // BUDGET meters per-tick dispatch cost — observe, drive, and mutate-over-section are the metered
        // capabilities; an untrusted principal (Addon/Peer) requires an explicit budget on those, a trusted
        // principal may not carry one, and budget:0 is refused outright. Mutate over a concrete state:<name> is a
        // different lane entirely (the cross-document durable-state write-back channel), gated by a write mask
        // rather than a budget, so a budget there is refused by name.
        var untrustedPrincipal = !IsTrusted(principal: grant.Principal);
        var meteredMutate = ((grant.Capability == WorldCapability.Mutate) && IsMutateDispatchSubject(subject: grant.Subject));
        var metered = ((grant.Capability is WorldCapability.Observe or WorldCapability.Drive) || meteredMutate);

        if (grant.Budget == 0) {
            reason = "budget:0 is refused — a granted-but-never-dispatched row is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (
            metered &&
            untrustedPrincipal &&
            (grant.Budget is null)
        ) {
            reason = $"an untrusted {Label(capability: grant.Capability)} grant to {grant.Principal.Describe()} requires an explicit budget:<n> — a defaulted per-tick dispatch allowance would silently decide a denial-of-service ceiling";

            return true;
        }

        if (grant.Budget is not null) {
            if (!metered) {
                reason = $"budget is refused on {Label(capability: grant.Capability)} {grant.Subject.Describe()} — only observe, drive, and mutate over a concrete section:<name>/creation:<id>/placement:<id> pass through a dispatch door there is anything to meter (a mutate state:<name> row is the cross-document write-back channel, gated by writes:<name,...> rather than by an allowance)";

                return true;
            }

            if (!untrustedPrincipal) {
                reason = $"budget is refused on {grant.Principal.Describe()}'s grant — trusted reads/drives are unmetered (console/seat, WorldServer.Answer, and the draw paths all stay ungated)";

                return true;
            }
        }

        // An untrusted principal's Mutate row over a concrete section requires an explicit verb mask: the admission
        // door reads an absent mask as full reach (required for Console's maskless boot rows), so an untrusted row
        // must say which kinds it reaches or be refused before it exists.
        //
        // An untrusted principal (Addon/Peer) is refused Mutate over section:rules or section:interactions outright:
        // both desugar into effects that fire as WorldPrincipal.World, admitted structurally with no budget or mask
        // enforcement — one gated act would otherwise launder unbounded, unmetered writes.
        if (
            untrustedPrincipal &&
            (grant.Capability == WorldCapability.Mutate) &&
            (grant.Subject.Kind == GrantSubjectKind.Section) &&
            ((((WorldSection)grant.Subject.Value) == WorldSection.Rules) || (((WorldSection)grant.Subject.Value) == WorldSection.Interactions))
        ) {
            var subjectNoun = ((((WorldSection)grant.Subject.Value) == WorldSection.Rules)
                ? "rule"
                : "interaction"
            );

            reason = $"an untrusted mutate grant to {grant.Principal.Describe()} over {grant.Subject.Describe()} is refused — a {subjectNoun}'s EFFECTS act as the world's own program, which the admission door admits structurally and never meters, so authoring a {subjectNoun} launders every budget and verb mask this row carries through one gated act; a verb mask cannot bound what the row does not dispatch";

            return true;
        }

        if (
            untrustedPrincipal &&
            (grant.Capability == WorldCapability.Mutate) &&
            IsMutateDispatchSubject(subject: grant.Subject) &&
            (grant.KindMask is null)
        ) {
            reason = $"an untrusted mutate grant to {grant.Principal.Describe()} over {grant.Subject.Describe()} requires an explicit verbs:<name,...> — an absent kind mask means FULL REACH at the admission door (a trusted principal's maskless row is the seeded default), so a maskless untrusted row would silently admit every kind {grant.Subject.Describe()} declares";

            return true;
        }

        // A row-scoped Mutate row reaches the ordered domain's apply door only (console, loopback, and the peer door
        // all converge on WorldServer.TryApplyMutation, which knows the mutation's target row). The addon mutation
        // seam designates a SECTION handle at its pre-flight and refuses any other subject as a stale handle, so a
        // row-scoped row granted to an addon could never dispatch: accepted-and-inert.
        if (
            (grant.Principal.Kind == PrincipalKind.Addon) &&
            (grant.Capability == WorldCapability.Mutate) &&
            (grant.Subject.Kind is GrantSubjectKind.Creation or GrantSubjectKind.Placement)
        ) {
            reason = $"a row-scoped mutate grant to {grant.Principal.Describe()} over {grant.Subject.Describe()} is refused — the addon mutation seam designates a section handle and refuses every other subject before decode, so this row would be accepted and inert; grant mutate section:{MaskSectionOf(subject: grant.Subject).ToString().ToLowerInvariant()} with budget:<n> verbs:<name,...> instead";

            return true;
        }

        // A row-scoped subject's id is never bound-checked against the live document (authoring a row that does not
        // exist yet is what a contribution slot grants), but its SHAPE still has to be able to match a row key.
        if (grant.Subject.Kind is GrantSubjectKind.Creation or GrantSubjectKind.Placement) {
            var rowId = (grant.Subject.Id ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: rowId)) {
                reason = $"{grant.Subject.Describe()} names a blank row id — no row can ever match it";

                return true;
            }

            // WorldCreation.Id is a DocumentIdentifier, so a `state.` token there is a REFERENCE whose resolved
            // value is some other string. WorldPlacement.Id is a plain literal, which is why this is creation-only.
            if (
                (grant.Subject.Kind == GrantSubjectKind.Creation) &&
                rowId.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix
            )
            ) {
                reason = $"{grant.Subject.Describe()} names a state reference rather than a row id — a '{Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix}' token resolves to some other string at load, so the row it addresses can never equal this subject; name the resolved id";

                return true;
            }
        }

        // The event budget meters event-push volume (a sibling of Budget, over the same row). Legal only on
        // Observe; events:0 is refused; required on Screen/Region/Seat subjects (which carry no other live meaning
        // under Observe), optional on Body (which keeps its pose-query meaning regardless).
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

        if (
            (grant.Capability == WorldCapability.Observe) &&
            (grant.EventBudget is null) &&
            (grant.Subject.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Region or GrantSubjectKind.Seat or GrantSubjectKind.Adjacency)
        ) {
            reason = $"observe {grant.Subject.Describe()} requires an explicit events:<n> — this subject carries no query meaning, only events, so a row with no event budget would be accepted-and-inert";

            return true;
        }

        if (grant.HoldCeiling is { } holdCeiling) {
            if (grant.Capability != WorldCapability.Drive) {
                reason = $"hold:<seconds> is refused on {Label(capability: grant.Capability)} — only a drive grant governs timed channel presses";

                return true;
            }

            if (
                (holdCeiling < 0L) ||
                (holdCeiling > Puck.Maths.FixedQ4816.FromDouble(value: WorldBody.MaxActionHoldSeconds).Value)
            ) {
                reason = $"hold:<seconds> must be within [0, {WorldBody.MaxActionHoldSeconds:0.###}] — the authored hold ceiling exceeds the engine backstop";

                return true;
            }
        }

        // Co-driving payload (Reach/Consent/Ceiling): a contributor row's Reach says which channels it may touch;
        // the occupying seat's own row (seatN drive body:N) carries a Ceiling — one number per (seat, channel),
        // never derived across contributor rows. Both fields apply only to a Drive row. ceiling:0 and an empty
        // channel set are refused. A Ceiling may only ride the seat's own row and must name its channels; a bare
        // Reach may only ride an untrusted contributor's row. The ceiling is range-pinned to raw [1, One] —
        // Puck.Maths.FixedContributionFold.Evaluate's pool clamp reads it directly, and it is what keeps an
        // untrusted pool from flipping a binary channel's bit against the human who owns it.
        if (
            ((grant.Reach is not null) || (grant.Consent is not null) || (grant.Ceiling is not null)) &&
            (grant.Capability != WorldCapability.Drive)
        ) {
            reason = $"channels/ceiling are refused on {Label(capability: grant.Capability)} — co-driving reach and the pooled ceiling only apply to a drive grant";

            return true;
        }

        if (grant.Ceiling == 0) {
            reason = "ceiling:0 is refused — a granted-but-never-reachable pool is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (
            (grant.Reach is { } emptyReach) &&
            emptyReach.IsEmpty
        ) {
            reason = "channels:<none> is refused — a reach naming no channel is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (
            (grant.Consent is { } emptyConsent) &&
            emptyConsent.IsEmpty
        ) {
            reason = "channels:<none> is refused — a consent gesture naming no channel is accepted-and-inert; grant nothing instead";

            return true;
        }

        if (grant.Ceiling is { } ceiling) {
            if (!IsOwnSeatBody(
                principal: grant.Principal,
                subject: grant.Subject
            )) {
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

            if (
                (ceiling < 0) ||
                (ceiling > Puck.Maths.FixedQ4816.One.Value)
            ) {
                reason = $"ceiling must be within [0, {Puck.Maths.FixedQ4816.One.Value}] raw (0..One) — {ceiling} is out of range";

                return true;
            }
        } else if (grant.Consent is not null) {
            reason = "consent channels must carry the positive ceiling they support";

            return true;
        } else if (
            (grant.Reach is not null) &&
            (grant.Principal.Kind is PrincipalKind.Console or PrincipalKind.Seat)
        ) {
            reason = $"a bare channel reach is refused on {grant.Principal.Describe()}'s row — a trusted contributor adds OUTSIDE the pool and is never masked; a seat's own row must name channels WITH a ceiling";

            return true;
        }

        // The kind mask (mutation-kind ordinals) is legal only on a Mutate row over a concrete section, or an Edit
        // row over a concrete state row — never the wildcard. A bit outside the target's own declared kind set
        // (WorldMutationKindCatalog.KindsOf) is refused, and so is a mask left admitting nothing (verbs:<none>).
        if (grant.KindMask is { } kindMask) {
            if (!CarriesKindMask(
                capability: grant.Capability,
                subject: grant.Subject
            )) {
                reason = $"a verb mask is refused on {Label(capability: grant.Capability)} {grant.Subject.Describe()} — only a mutate grant over a concrete section:<name>/creation:<id>/placement:<id>, or an edit grant over a concrete state:<name>, carries one";

                return true;
            }

            var admissible = WorldMutationKindCatalog.KindsOf(section: MaskSectionOf(subject: grant.Subject));
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

        // The write mask (WorldDocumentWriteKind operations) is legal only on a Mutate row over a concrete state
        // subject — the cross-document durable-state channel. A separate check from the kind mask above.
        if (grant.WriteMask is { } writeMask) {
            if (!CarriesWriteMask(
                capability: grant.Capability,
                subject: grant.Subject
            )) {
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

        // A different principal's exclusive reservation of an overlapping subject blocks either order. The ordinary
        // `all` wildcard is exempt: without it, an exclusive concrete hold taken elsewhere would permanently block
        // the wildcard's later re-grant, since the revoke that could undo it targets the wildcard, never the
        // concrete subject actually reserved. Allows already makes the exclusive holder the sole effective owner of
        // its own subject, so this exemption costs no enforcement.
        if (!(!grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All))) {
            foreach (var pair in m_exclusive) {
                if (
                    (pair.Key.Capability == grant.Capability) &&
                    (pair.Value != grant.Principal) &&
                    SubjectsOverlap(
                    a: grant.Subject,
                    b: pair.Key.Subject
                )
                ) {
                    reason = $"{grant.Subject.Describe()} conflicts with {pair.Value.Describe()}'s exclusive {Label(capability: grant.Capability)} {pair.Key.Subject.Describe()}";

                    return true;
                }
            }
        }

        // An incoming exclusive grant also rejects when a different principal already holds the same concrete
        // subject ordinarily. The wildcard `all` and the seeded per-section Mutate rows are exempt (both are the
        // permissive backdrop, not a real hold); a section row that was revoked and re-granted blocks like any
        // other, since Revoke is the only place the seed marker dies.
        if (
            grant.Exclusive &&
            (grant.Subject.Kind != GrantSubjectKind.All)
        ) {
            foreach (var pair in m_byPrincipal) {
                if (
                    (pair.Key != grant.Principal) &&
                    (pair.Value.For(capability: grant.Capability)?.Contains(item: grant.Subject) == true) &&
                    !m_seededSections.Contains(item: new SeededKey(
                    Principal: pair.Key,
                    Capability: grant.Capability,
                    Subject: grant.Subject
                ))
                ) {
                    reason = $"{grant.Subject.Describe()} is already held by {pair.Key.Describe()}";

                    return true;
                }
            }
        }

        return false;
    }
    // The principal that exclusively reserves `subject` for `capability`, considering the `all` wildcard reservation (an
    // exclusive `all` reserves every concrete subject of the capability). Null when the subject is unreserved — the
    // normal wildcard/subject-set logic then applies. A query for `all` itself only matches an EXACT `all` reservation:
    // a concrete exclusive body does not lock the whole-domain query the permissive Edit check makes.
    private WorldPrincipal? ExclusiveHolderOf(WorldCapability capability, GrantSubject subject) {
        if (m_exclusive.TryGetValue(
            key: new ExclusiveKey(
                Capability: capability,
                Subject: subject
            ),
            value: out var exact
        )) {
            return exact;
        }

        if (
            (subject.Kind != GrantSubjectKind.All) &&
            m_exclusive.TryGetValue(
            key: new ExclusiveKey(
                Capability: capability,
                Subject: GrantSubject.All
            ),
            value: out var wildcard
        )
        ) {
            return wildcard;
        }

        return null;
    }
    // The per-capability POSITIVE rule for which subject shapes a grant may legitimately name — a NEW
    // GrantSubjectKind or WorldCapability is refused by default here rather than silently admitted. A concrete
    // subject in a capability's own domain (a body for Drive, a screen for Control, a section for Mutate, a state
    // row for Edit) is legitimate for any principal. The `all` wildcard is legitimate only for a principal inside
    // the local trust boundary for that capability — Console and Seat generally, plus Peer additionally for
    // Control. A Drive or Observe Body is additionally bounded to an index the population actually holds.
    private bool IsLegitimateSubject(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        var trustedWildcard = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);

        return capability switch {
            WorldCapability.Drive => (((subject.Kind == GrantSubjectKind.Body) && (((uint)subject.Value) < ((uint)m_population))) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard)),
            // Observe additionally admits Screen/Region/Seat/Adjacency, untrusted principals only — the event-only
            // subject kinds the world-events feed gates: a screen for machine-memory watches, a region for
            // enter/exit, a local seat for join/leave, an adjacency row for the federation link family. Region and
            // Adjacency are unbounded (an unknown name simply never fires); Seat is bounded to the reserved
            // local-seat band.
            WorldCapability.Observe => (((subject.Kind == GrantSubjectKind.Body) && (((uint)subject.Value) < ((uint)m_population))) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Screen)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Region)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Adjacency)) ||
                (!trustedWildcard && (subject.Kind == GrantSubjectKind.Seat) && (((uint)subject.Value) < ((uint)WorldPopulationLimits.LocalSeatCount))) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard)),
            WorldCapability.Control => ((subject.Kind == GrantSubjectKind.Screen) ||
                // A control application's target may be a BODY — a possession/co-drive application, bounded by the
                // population exactly like Drive/Observe's own body subjects.
                ((subject.Kind == GrantSubjectKind.Body) && (((uint)subject.Value) < ((uint)m_population))) ||
                ((subject.Kind == GrantSubjectKind.Composition) && trustedWildcard) ||
                ((subject.Kind == GrantSubjectKind.All) && (trustedWildcard || (principal.Kind == PrincipalKind.Peer)))),
            // Mutate additionally admits the two ROW-SCOPED dispatch subjects — one creations row, one placements
            // row — for any principal. They are an alternative to the section hold, never a narrowing beneath it,
            // and the id is shape-checked rather than bound-checked (a contribution slot grants the right to author
            // a row that does not exist yet).
            WorldCapability.Mutate => ((subject.Kind is GrantSubjectKind.Section or GrantSubjectKind.State or GrantSubjectKind.Creation or GrantSubjectKind.Placement) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard)),
            WorldCapability.Edit => ((subject.Kind == GrantSubjectKind.State) ||
                ((subject.Kind == GrantSubjectKind.All) && trustedWildcard)),
            _ => false,
        };
    }
    // The occupying seat's own row — the pairing that may author a pooled ceiling, and the one a Seat actor may
    // administer at all. A local seat's binding to a body is fixed identity (slot n is body index n).
    private static bool IsOwnSeatBody(WorldPrincipal principal, GrantSubject subject) {
        return (
            (principal.Kind == PrincipalKind.Seat) &&
            (subject.Kind == GrantSubjectKind.Body) &&
            (subject.Value == principal.Index)
        );
    }
    // The positive statement of what a handle table may designate: exactly the kinds that name ONE instance of a
    // capability's domain — one body, one screen, one section, one state row. Everything else (including All and
    // Composition, which are whole-domain designations) is withheld by default.
    private static bool IsProjectable(GrantSubject subject) =>
        (subject.Kind is GrantSubjectKind.Body or GrantSubjectKind.Screen or GrantSubjectKind.Section or GrantSubjectKind.State);
    private static string Label(WorldCapability capability) => capability.ToString().ToLowerInvariant();
    // Drops every composed application whose target this principal no longer holds Control over, restoring the
    // own-body application when that leaves the set with nothing else. The own-body member itself is never dropped
    // here: an application set with no own body IS capture, and losing an unrelated grant must not capture an
    // avatar. Whoever revoked the hold already exercised the authority this teardown is a consequence of.
    private void DissolveUnauthorizedApplications(WorldPrincipal principal) {
        if (!m_applications.TryGetValue(
            key: principal,
            value: out var composed
        )) {
            return;
        }

        var own = GrantSubject.Body(index: principal.Index);
        var survivors = new List<ControlApplication>(capacity: composed.Count);

        foreach (var application in composed) {
            if (
                (application.Target == own) ||
                Allows(
                capability: WorldCapability.Control,
                principal: principal,
                subject: application.Target
            ).IsAllowed
            ) {
                survivors.Add(item: application);
            }
        }

        if (survivors.Count == composed.Count) {
            return;
        }

        if (survivors.Count == 0) {
            survivors.AddRange(collection: DefaultApplications(principal: principal));
        }

        SetApplications(
            applications: survivors,
            principal: principal
        );
    }
    // The application set a participant holds when it has composed nothing: its own body alone, passthrough over
    // every ordinal. Cached per body index so the per-tick fold's read allocates nothing; a principal with no body
    // of its own (Console, Addon, World) applies to nothing by default.
    private static IReadOnlyList<ControlApplication> DefaultApplications(WorldPrincipal principal) {
        if (principal.Kind is not (PrincipalKind.Seat or PrincipalKind.Peer)) {
            return [];
        }

        var index = principal.Index;

        if (((uint)index) >= ((uint)s_ownBodyApplications.Length)) {
            return [];
        }

        return (s_ownBodyApplications[index] ??= [ControlApplication.OwnBody(bodyIndex: index)]);
    }
    private static bool Holds(IReadOnlyList<ControlApplication> applications, GrantSubject target) {
        for (var index = 0; (index < applications.Count); index++) {
            if (applications[index].Target == target) {
                return true;
            }
        }

        return false;
    }
    private void NotifyApplicationTransition(WorldPrincipal principal, GrantSubject? previous, GrantSubject? current) {
        if (previous == current) {
            return;
        }

        m_routeTransition(
            principal,
            previous,
            current
        );
    }
    private static bool SameApplications(IReadOnlyList<ControlApplication> first, IReadOnlyList<ControlApplication> second) {
        if (first.Count != second.Count) {
            return false;
        }

        for (var index = 0; (index < first.Count); index++) {
            if (first[index] != second[index]) {
                return false;
            }
        }

        return true;
    }
    // Non-Drive permissive defaults shared by seats and the console: Observe over every subject, Control over every
    // screen, Mutate over every section except Grants (the console alone is seeded over it, see below), and Edit
    // over the `all` wildcard (Edit's domain is state:<name>).
    private void SeedDomain(WorldPrincipal principal) {
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: principal,
                Capability: WorldCapability.Observe,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            reason: out _
        );
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: principal,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            reason: out _
        );
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: principal,
                Capability: WorldCapability.Edit,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            reason: out _
        );
        // The shared window-composition authority — seats and the console can drive the live view.override layout/view.override camera
        // overrides (peers, who get only Control/all above, do not receive this concrete grant). A director can still
        // acquire it exclusively over this concrete subject to own the shot.
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: principal,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.Composition,
                Exclusive: false
            ),
            reason: out _
        );

        foreach (var section in Enum.GetValues<WorldSection>()) {
            // Grants is META-authority: mutating it authors who may do what. Only the console is seeded over it; a
            // seat that needs it is granted it deliberately, and AllowsAllSections cannot pass for a seat either.
            if (
                (section == WorldSection.Grants) &&
                (principal.Kind != PrincipalKind.Console)
            ) {
                continue;
            }

            var subject = GrantSubject.Section(section: section);

            _ = TryGrant(
                grant: new WorldGrant(
                    Principal: principal,
                    Capability: WorldCapability.Mutate,
                    Subject: subject,
                    Exclusive: false
                ),
                reason: out _
            );
            // Mark the row as SEED so it never blocks another principal's exclusive section hold (see the type doc's
            // acquisition rules) — the backdrop must never block a reservation, exactly like the ordinary `all` wildcard.
            _ = m_seededSections.Add(item: new SeededKey(
                Capability: WorldCapability.Mutate,
                Principal: principal,
                Subject: subject
            ));
        }
    }
    // The human-readable half of an illegitimate-subject rejection — names ONLY the shapes IsLegitimateSubject
    // actually admits for this (principal, capability), so the message can never claim a shape the rule does not
    // grant. Conflicts' `label` (built by the caller in WorldServer.Grant) already carries the rejected
    // principal/capability/subject, so this never repeats them.
    private string SubjectRule(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        // The ONE illegitimate Body case for the three body-domain capabilities is an out-of-range index (a Body
        // subject is otherwise always legitimate for them, for any principal) — name the actual population ceiling
        // rather than the generic body:<n> shape the operator already spelled correctly.
        if (
            (capability is WorldCapability.Drive or WorldCapability.Observe or WorldCapability.Control) &&
            (subject.Kind == GrantSubjectKind.Body)
        ) {
            return $"body:{subject.Value} does not exist — the population holds 0..{(m_population - 1)}";
        }

        // The ONE illegitimate Seat case (Observe only) is likewise an out-of-range index, not a wrong shape.
        if (
            (capability == WorldCapability.Observe) &&
            (subject.Kind == GrantSubjectKind.Seat)
        ) {
            return $"seat:{subject.Value} does not exist — local seats are 0..{(WorldPopulationLimits.LocalSeatCount - 1)}";
        }

        var trusted = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);

        return capability switch {
            WorldCapability.Drive => ((principal.Kind == PrincipalKind.Addon)
            ? "an addon must name the concrete body it drives (drive body:<n>)"
            : $"drive must name a concrete body (drive body:<n>){(trusted
                ? " or the wildcard 'all'"
                : "")}"),
            WorldCapability.Observe => (trusted
            ? "observe must name a concrete body (observe body:<n>) or the wildcard 'all' — screen/region/seat/adjacency are event-only subjects with no trusted-principal consumer"
            : "observe must name a concrete body, screen, region, seat, or adjacency (observe body:<n> | observe screen:<n> | observe region:<name> | observe seat:<n> | observe adjacency:<name>)"),
            WorldCapability.Control => $"control must name a concrete screen or body (control screen:<n> | control body:<n>){((trusted || (principal.Kind == PrincipalKind.Peer))
            ? " or the wildcard 'all'"
            : "")}",
            WorldCapability.Mutate => $"mutate must name a document section (mutate section:<name>), one creations or placements row (mutate creation:<id> | mutate placement:<id>), or a concrete state row for the cross-document write-back channel (mutate state:<name>){(trusted
            ? ", or the wildcard 'all'"
            : "")}",
            WorldCapability.Edit => $"edit must name a concrete state row (edit state:<name>){(trusted
            ? " or the wildcard 'all'"
            : "")}",
            _ => "this capability accepts no subject today",
        };
    }
    // Two subjects overlap when identical or either is the `all` wildcard. Used only for exclusive-reservation
    // conflicts — the ordinary wildcard backdrop is exempt and checked separately.
    //
    // The wildcard expansion belongs in this comparison, never in storage: expanding it into what is stored would
    // destroy verdict distinction, revocation identity, and the zero-slot projection. Any future rewrite of this
    // comparison inherits that constraint.
    private static bool SubjectsOverlap(GrantSubject a, GrantSubject b) {
        return (
            (a == b) ||
            (a.Kind == GrantSubjectKind.All) ||
            (b.Kind == GrantSubjectKind.All)
        );
    }

    /// <inheritdoc/>
    /// <remarks>Membership expansion — grown into this one predicate rather than duplicated at each door, per the
    /// group+binding substrate's own design rule. Checked last, after the principal's own concrete/wildcard rows
    /// miss: a principal's own hold always wins first, and a group's hold is a fallback, never an override. Flat
    /// only means this never recurses — a group entry in <see cref="m_groupMembership"/>'s value list is itself
    /// checked by looking up its own rows only, never by treating the group as a further member of anything.
    /// <para>Ownership consult (composition-core): the same fallback shape, grown a second way, checked after
    /// membership — an ownership binding is a deciding fact this door consults, never a grant row
    /// <see cref="WorldGrants"/> mints; <c>Puck.World.WorldOwnership</c> seeds/implies authority, it never is a
    /// grant. Safe to fold unconditionally into every <see cref="Allows"/> caller (unlike Seam A's drive gate,
    /// which refuses and is therefore scoped to the intent-admission door alone — see
    /// <see cref="GrantRule.DriveGated"/>): ownership only ever adds reach, so no existing caller's denial can flip
    /// to an unwanted allow, and no existing caller's allow is ever taken away.</para></remarks>
    public GrantVerdict Allows(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        // The exclusivity override: a reserved subject answers for its reserver ALONE, so an exclusively-held body has
        // exactly one effective driver even though the console still holds the seeded Drive/all wildcard.
        if (ExclusiveHolderOf(
            capability: capability,
            subject: subject
        ) is { } holder) {
            return ((holder == principal)
                ? new GrantVerdict(Rule: GrantRule.ReserverMatch)
                : new GrantVerdict(
                    Rule: GrantRule.BeatenByReserver,
                    Reserver: holder
                )
            );
        }

        if (
            m_byPrincipal.TryGetValue(
            key: principal,
            value: out var grants
        ) &&
            (grants.For(capability: capability) is { } subjects)
        ) {
            if (subjects.Contains(item: subject)) {
                return new GrantVerdict(Rule: GrantRule.ConcreteHold);
            }

            if (subjects.Contains(item: GrantSubject.All)) {
                return new GrantVerdict(Rule: GrantRule.WildcardHold);
            }
        }

        // Group-expansion fallback: does a group `principal` is currently a member of hold this (capability,
        // subject)? Read fresh from m_groupMembership each call, so a departed member's hold evaporates immediately.
        if (TryGroupExpansion(
            capability: capability,
            groups: m_groupMembership,
            principal: principal,
            rule: GrantRule.GroupHold,
            subject: subject,
            verdict: out var membershipVerdict
        )) {
            return membershipVerdict;
        }

        // Ownership-expansion fallback: does a group `principal` currently owns (direct or transitive, resolved at
        // sync time into m_ownedGroups) hold this (capability, subject)? Read fresh, same as the membership fallback.
        if (TryGroupExpansion(
            capability: capability,
            groups: m_ownedGroups,
            principal: principal,
            rule: GrantRule.OwnershipHold,
            subject: subject,
            verdict: out var ownershipVerdict
        )) {
            return ownershipVerdict;
        }

        return new GrantVerdict(Rule: GrantRule.NoHold);
    }
    // The shared body of the group-membership and group-ownership expansion fallbacks above: does any group listed
    // for `principal` in `groups` hold (capability, subject) or its All wildcard, itself resolved fresh through
    // m_byPrincipal on every call (never cached). `rule` names which fallback is calling, so the returned verdict
    // still distinguishes GroupHold from OwnershipHold.
    private bool TryGroupExpansion(Dictionary<WorldPrincipal, List<string>> groups, WorldPrincipal principal, WorldCapability capability, GrantSubject subject, GrantRule rule, out GrantVerdict verdict) {
        if (groups.TryGetValue(
            key: principal,
            value: out var groupIds
        )) {
            foreach (var groupId in groupIds) {
                var groupPrincipal = WorldPrincipal.Group(id: groupId);

                if (
                    !m_byPrincipal.TryGetValue(
                    key: groupPrincipal,
                    value: out var groupGrants
                ) ||
                    (groupGrants.For(capability: capability) is not { } groupSubjects)
                ) {
                    continue;
                }

                if (
                    groupSubjects.Contains(item: subject) ||
                    groupSubjects.Contains(item: GrantSubject.All)
                ) {
                    verdict = new GrantVerdict(
                        Rule: rule,
                        Group: groupId
                    );

                    return true;
                }
            }
        }

        verdict = default;

        return false;
    }
    /// <inheritdoc/>
    public bool AllowsAllSections(WorldPrincipal principal, WorldCapability capability, out WorldSection deniedSection, out GrantVerdict denial) {
        foreach (var section in Enum.GetValues<WorldSection>()) {
            if (Allows(
                principal: principal,
                capability: capability,
                subject: GrantSubject.Section(section: section)
            ) is { IsAllowed: false } verdict) {
                deniedSection = section;
                denial = verdict;

                return false;
            }
        }

        deniedSection = default;
        denial = default;

        return true;
    }
    /// <inheritdoc/>
    public IReadOnlyList<ControlApplication> Applications(WorldPrincipal principal) {
        return (m_applications.TryGetValue(
            key: principal,
            value: out var composed
        )
            ? composed
            : DefaultApplications(principal: principal)
        );
    }
    /// <inheritdoc/>
    public bool ClearApplications(WorldPrincipal principal) {
        if (!m_applications.TryGetValue(
            key: principal,
            value: out var composed
        )) {
            return false;
        }

        _ = m_applications.Remove(key: principal);

        foreach (var dissolved in composed) {
            NotifyApplicationTransition(
                current: null,
                previous: dissolved.Target,
                principal: principal
            );
        }

        foreach (var restored in DefaultApplications(principal: principal)) {
            NotifyApplicationTransition(
                current: restored.Target,
                previous: null,
                principal: principal
            );
        }

        return true;
    }
    /// <inheritdoc/>
    public void CollectApplicationHolders(GrantSubject target, List<WorldPrincipal> into) {
        into.Clear();

        foreach (var pair in m_applications) {
            foreach (var application in pair.Value) {
                if (application.Target == target) {
                    into.Add(item: pair.Key);

                    break;
                }
            }
        }
    }
    /// <inheritdoc/>
    public string Describe(WorldPrincipal? filter) {
        var builder = new StringBuilder(value: "[world.grants:");
        var any = false;

        foreach (var pair in m_byPrincipal) {
            if (
                (filter is { } only) &&
                (pair.Key != only)
            ) {
                continue;
            }

            var held = Held(principal: pair.Key);

            if (held.Count == 0) {
                continue;
            }

            _ = builder.Append(value: (any
                ? " | "
                : " ")).Append(value: pair.Key.Describe()).Append(value: ' ');

            for (var index = 0; (index < held.Count); index++) {
                var (capability, subject) = held[index];
                var isExclusive = (m_exclusive.TryGetValue(
                    key: new ExclusiveKey(
                        Capability: capability,
                        Subject: subject
                    ),
                    value: out var holder
                ) && (holder == pair.Key));

                if (index > 0) {
                    _ = builder.Append(value: ' ');
                }

                _ = builder.Append(value: capability.ToString().ToLowerInvariant()).Append(value: '/').Append(value: subject.Describe());

                if (isExclusive) {
                    _ = builder.Append(value: "(x)");
                }

                if (m_budgets.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var budget
                )) {
                    _ = builder.Append(value: " budget:").Append(value: budget);
                }

                if (m_eventBudgets.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var eventBudget
                )) {
                    _ = builder.Append(value: " events:").Append(value: eventBudget);
                }

                if (capability == WorldCapability.Drive) {
                    var holdCeiling = (m_holdCeilings.TryGetValue(
                        key: (pair.Key, capability, subject),
                        value: out var authoredHold
                    )
                        ? authoredHold
                        : DefaultHoldCeiling
                    );

                    _ = builder.Append(value: " hold:").Append(value: ((double)Puck.Maths.FixedQ4816.FromRawBits(value: holdCeiling)).ToString(
                        format: "0.###",
                        provider: CultureInfo.InvariantCulture
                    ));
                }

                if (m_channelReach.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var reach
                )) {
                    _ = builder.Append(value: " channels:0x").Append(value: reach.Bits.ToString(format: "x"));
                }

                // NAMES, never a hex lane: a read-back an operator cannot decode by eye is a read-back that does
                // not close the loop the authoring token opened (world.grant takes verbs:<Name,...>/writes:<Name,...>).
                if (m_kindMasks.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var kindMask
                )) {
                    _ = builder.Append(value: " verbs:").Append(value: kindMask.Describe());
                }

                if (m_writeMasks.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var writeMask
                )) {
                    _ = builder.Append(value: " writes:").Append(value: writeMask.Describe());
                }

                // The seat's authored ceilings render per ORDINAL, never as one scalar: a row carrying a `forward`
                // ceiling and a different `turn` ceiling has to read as the two numbers it is.
                if (m_poolCeilings.TryGetValue(
                    key: (pair.Key, capability, subject),
                    value: out var ceilings
                )) {
                    var first = true;

                    for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                        if (ceilings[ordinal] == 0L) {
                            continue;
                        }

                        _ = builder.Append(value: (first
                            ? " ceilings:"
                            : ",")).Append(value: ordinal).Append(value: '=').Append(value: ceilings[ordinal]);
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
    /// <inheritdoc/>
    public WorldPrincipal? ExclusiveHolder(WorldCapability capability, GrantSubject subject) =>
        ExclusiveHolderOf(
            capability: capability,
            subject: subject
        );
    /// <inheritdoc/>
    public WorldHandleTable HandleTable(WorldPrincipal principal, WorldCapability capability) {
        var key = (Principal: principal, Capability: capability);

        if (!m_handleTables.TryGetValue(
            key: key,
            value: out var table
        )) {
            table = new WorldHandleTable(
                capability: capability,
                grants: this,
                principal: principal
            );
            m_handleTables[key] = table;
        }

        return table;
    }
    /// <inheritdoc/>
    public IReadOnlyList<(WorldCapability Capability, GrantSubject Subject)> Held(WorldPrincipal principal) {
        if (!m_byPrincipal.TryGetValue(
            key: principal,
            value: out var grants
        )) {
            return [];
        }

        var held = new List<(WorldCapability, GrantSubject)>();

        foreach (var capability in CapabilityOrder) {
            if (grants.For(capability: capability) is not { Count: > 0 } subjects) {
                continue;
            }

            var projected = new GrantSubject[subjects.Count];

            subjects.CopyTo(array: projected);
            Array.Sort(
                array: projected,
                comparison: CompareSubjects
            );

            foreach (var subject in projected) {
                held.Add(item: (capability, subject));
            }
        }

        return held;
    }
    /// <inheritdoc/>
    public long HoldCeiling(WorldPrincipal principal, GrantSubject subject) {
        var verdict = Allows(
            capability: WorldCapability.Drive,
            principal: principal,
            subject: subject
        );
        var decidingSubject = ((verdict.Rule == GrantRule.WildcardHold)
            ? GrantSubject.All
            : subject
        );

        return (m_holdCeilings.TryGetValue(
            key: (principal, WorldCapability.Drive, decidingSubject),
            value: out var ceiling
        )
            ? ceiling
            : DefaultHoldCeiling
        );
    }
    /// <summary>Determines whether <paramref name="principal"/> may administer (grant or revoke) <paramref name="capability"/> over
    /// <paramref name="subject"/> — the <c>world.grant</c>/<c>world.revoke</c> actor test, distinct from
    /// <see cref="Allows"/>. Enforced only for principal kinds outside the trust boundary
    /// (<see cref="PrincipalKind.Addon"/>, <see cref="PrincipalKind.Peer"/>): those must hold the administered
    /// <c>(capability, subject)</c> themselves (ignoring exclusivity — see below), so a delegated administrator can
    /// never hand out authority it does not itself have. <see cref="PrincipalKind.Console"/> and
    /// <see cref="PrincipalKind.Seat"/> pass unconditionally: both sit inside the trust boundary — an operator who can
    /// already grant themselves anything — so gating self-administration there is ceremony that costs real
    /// functionality (self-revocation would become a one-way ratchet; a seeded per-section grant could never be
    /// re-issued as the `all` wildcard) and buys nothing while the actor stays a caller-asserted parameter hardcoded
    /// at `WorldGrantCommandModule`'s one call site — this stops being inert the day a `Peer` (or an untrusted
    /// `Addon`) can reach `IServerLink.SubmitGrant`/`SubmitRevoke` directly.
    ///
    /// For the kinds this does gate: membership ignores exclusivity, because <see cref="Allows"/>'s exclusivity
    /// override answers "does this principal effectively wield the capability right now" — correct for use, wrong for
    /// administration, where the principal who granted an exclusive hold must still be able to revoke it.</summary>
    /// <param name="principal">The acting identity administering the grant (the actor, never the grant's subject).</param>
    /// <param name="capability">The capability the administered grant confers.</param>
    /// <param name="subject">The subject the administered grant scopes to.</param>
    /// <remarks><b><see cref="PrincipalKind.Console"/> and <see cref="PrincipalKind.Seat"/> are not symmetric here.</b>
    /// Console passes unconditionally — it sits inside the trust boundary and gating self-administration there is
    /// ceremony. Seat does not: co-driving consent is a grant row (<see cref="WorldGrant.Consent"/>/<see cref="WorldGrant.Ceiling"/>),
    /// so an unconditional Seat pass would let the enable flow work only because this door was wider than the feature
    /// needed. A Seat may administer a grant row only where the subject is its own body — any capability (Drive,
    /// Observe, or Control) naming its own seat index, since <see cref="IsOwnSeatBody"/> is capability-blind —
    /// exactly what "enabling an addon on your own seat is the consent gesture" requires and nothing beyond it: a
    /// Seat actor administering any other subject (another seat's body, a section, a screen, a state row)
    /// refuses.</remarks>
    public bool HoldsForAdministration(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        if (principal.Kind == PrincipalKind.Console) {
            return true;
        }

        if (principal.Kind == PrincipalKind.Seat) {
            return IsOwnSeatBody(
                principal: principal,
                subject: subject
            );
        }

        if (!m_byPrincipal.TryGetValue(
            key: principal,
            value: out var grants
        )) {
            return false;
        }

        var subjects = grants.For(capability: capability);

        return (
            (subjects is not null) &&
            (subjects.Contains(item: GrantSubject.All) || subjects.Contains(item: subject))
        );
    }
    /// <summary>Determines whether any principal holds Drive concretely over <paramref name="body"/> — a possession, as
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
    /// <summary>Determines whether a principal sits inside the local trust boundary for administration and metering — the
    /// boundary the grant door's budget/mask requirements and <see cref="WorldServer.TryAdmitMutation"/>'s budget gate
    /// both read.</summary>
    /// <remarks>Written as the positive admission list (Console and Seat) rather than "untrusted" as a named
    /// exclusion, so a principal kind added later is untrusted by the complement and can never slip through as
    /// trusted-by-omission. This is not the fold's contributor-trust predicate: that one keys on host locus and
    /// counts a document-mounted <see cref="PrincipalKind.Addon"/> as trusted (see
    /// <c>WorldServer.StageContribution</c>). Conflating the two is the wrong-answer trap this repository's authority
    /// notes name explicitly — they diverge on <see cref="PrincipalKind.Addon"/>.</remarks>
    /// <param name="principal">The principal to classify.</param>
    /// <returns><see langword="true"/> for <see cref="PrincipalKind.Console"/> and <see cref="PrincipalKind.Seat"/>.</returns>
    public static bool IsTrusted(WorldPrincipal principal) => (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);
    /// <inheritdoc/>
    public ChannelCeilings PoolCeilings(WorldPrincipal seat, GrantSubject subject) =>
        (m_poolCeilings.TryGetValue(
            key: (seat, WorldCapability.Drive, subject),
            value: out var ceilings
        )
            ? ceilings
            : default
        );
    /// <inheritdoc/>
    /// <remarks>
    /// Sorted by (<see cref="GrantSubjectKind"/>, value, id) rather than returned in <see cref="HashSet{T}"/>
    /// enumeration order, which is a free-list/insertion-history artifact that is not stable across a rebuild ending
    /// at an identical subject set — a genuinely deterministic tie-break rather than "whatever order the BCL happens
    /// to hand back", which is reproducible run-to-run for a fixed grant/revoke sequence but not stable across a
    /// different sequence that ends at the same subject set.
    /// <para><b>Only a per-instance subject kind is ever projected — the legal set is stated positively
    /// (<see cref="IsProjectable"/>), so a new kind is withheld by default.</b> A handle table's whole premise — "a
    /// guest still cannot name what it was not handed" — assumes every slot names one instance of the capability's
    /// domain; projecting a whole-domain designation would hand out a single index that designates everything the
    /// principal holds, and because low kinds sort first it would land at the most convenient index a naive guest asks
    /// for (a population <see cref="PrincipalKind.Peer"/>'s
    /// seeded <see cref="WorldCapability.Control"/>/<see cref="GrantSubject.All"/> is exactly this case the moment a
    /// <c>Control</c> handle table exists). A negation rule — "never
    /// <see cref="GrantSubjectKind.All"/>" — is the wrong shape: <see cref="GrantSubjectKind.Composition"/> is also a
    /// whole-domain designation (its own doc: not a body, a screen, or a section) and would sail through such a
    /// filter, since the seeded seat rows project it too. The positive
    /// statement lives here rather than at each capability's <c>IsLegitimateSubject</c> door, because holding a
    /// whole-domain subject is legitimate, real authority for some (principal, capability) pairs today (a peer's
    /// boot-seeded <c>Control</c>/<c>all</c> route, a seat's <c>Control</c>/<c>composition</c>) — it is refused only
    /// from projection, never from the grant table itself.</para></remarks>
    public GrantSubject[] ProjectSubjects(WorldPrincipal principal, WorldCapability capability) {
        if (
            !m_byPrincipal.TryGetValue(
            key: principal,
            value: out var grants
        ) ||
            (grants.For(capability: capability) is not { } subjects)
        ) {
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

        Array.Sort(
            array: projected,
            comparison: CompareSubjects
        );

        return projected;
    }
    /// <summary>Clears every held row — concrete and wildcard holds, exclusive reservations, budgets, channel
    /// reach/ceilings, verb masks, the seeded-section marker set, and the handle-table cache — then re-seeds the
    /// permissive local-play defaults exactly as the constructor does, silently (via <see cref="TryGrant"/> directly,
    /// never the loud <c>Server.WorldServer.Grant</c> door — identical to how the constructor's own seed is silent).
    /// The runtime half of a whole-document rebuild (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>):
    /// "runtime grants drop; document grants re-apply as at boot." The document's own <c>Grants</c> section is
    /// deliberately not replayed here — that half needs <see cref="Server.WorldServer.WithoutAuthoredConsent"/> and
    /// the loud <c>Grant</c> door, so the caller replays it immediately afterward exactly as the constructor's own
    /// body does, and re-mints every currently-admitted peer connection's admission grant afterward still (a
    /// peer is a connection, not a document row or a boot-time seat, so nothing here or in the document replay
    /// re-establishes it).</summary>
    /// <param name="seatCount">The reserved local-seat count — identical to the value passed at construction.</param>
    public void Reset(int seatCount) {
        var droppedApplications = new List<(WorldPrincipal Principal, GrantSubject Target)>();

        foreach (var (principal, applications) in m_applications) {
            foreach (var application in applications) {
                droppedApplications.Add(item: (principal, application.Target));
            }
        }

        m_applications.Clear();
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

        foreach (var (principal, target) in droppedApplications) {
            NotifyApplicationTransition(
                current: null,
                previous: target,
                principal: principal
            );
        }

        for (var slot = 0; (slot < seatCount); slot++) {
            var seat = WorldPrincipal.Seat(slot: slot);

            _ = TryGrant(
                grant: new WorldGrant(
                    Principal: seat,
                    Capability: WorldCapability.Drive,
                    Subject: GrantSubject.Body(index: slot),
                    Exclusive: false
                ),
                reason: out _
            );
            SeedDomain(principal: seat);
        }

        SeedDomain(principal: WorldPrincipal.Console);
        _ = TryGrant(
            grant: new WorldGrant(
                Principal: WorldPrincipal.Console,
                Capability: WorldCapability.Drive,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            reason: out _
        );
    }
    /// <summary>Removes a grant (capability+subject) from a principal, and clears any matching exclusive reservation.
    /// A no-op that returns <see langword="false"/> when the principal did not hold it. Not part of
    /// <see cref="IWorldGrantsView"/>: this is an authority door, reached only through
    /// <see cref="WorldServer.Revoke"/>'s <see cref="HoldsForAdministration"/> actor check, never through the view a
    /// non-<see cref="WorldServer"/> caller holds.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to revoke.</param>
    /// <param name="subject">The subject to revoke.</param>
    /// <returns>Whether a grant was actually removed.</returns>
    public bool Revoke(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        var removed = false;

        if (m_byPrincipal.TryGetValue(
            key: principal,
            value: out var grants
        )) {
            removed = grants.Remove(
                capability: capability,
                subject: subject
            );
        }

        var key = new ExclusiveKey(
            Capability: capability,
            Subject: subject
        );

        if (
            m_exclusive.TryGetValue(
            key: key,
            value: out var holder
        ) &&
            (holder == principal)
        ) {
            _ = m_exclusive.Remove(key: key);
        }

        // The seed marker dies with the row: a re-grant after this revoke is a deliberate hold and blocks exclusive
        // acquisition like any other.
        _ = m_seededSections.Remove(item: new SeededKey(
            Capability: capability,
            Principal: principal,
            Subject: subject
        ));

        // A revoked row's dispatch budget dies with it: a later re-grant that carries no budget must not inherit
        // one a prior, now-revoked hold left behind.
        _ = m_budgets.Remove(key: (principal, capability, subject));
        _ = m_eventBudgets.Remove(key: (principal, capability, subject));
        _ = m_holdCeilings.Remove(key: (principal, capability, subject));

        // A revoked row's co-driving reach and authored ceiling vector die with it too. Revoking the seat's own
        // Drive row is the only way to clear an authored ceiling — a ceiling gesture writes only the ordinals it
        // names and leaves the rest alone, and ceiling:0 is refused at the door.
        _ = m_channelReach.Remove(key: (principal, capability, subject));
        _ = m_poolCeilings.Remove(key: (principal, capability, subject));

        // A revoked row's masks die with it too, same as its budget/reach/ceiling.
        _ = m_kindMasks.Remove(key: (principal, capability, subject));
        _ = m_writeMasks.Remove(key: (principal, capability, subject));

        if (removed) {
            // A handle designating this (capability, subject) must resolve to nothing on its very next use — see
            // WorldHandleTable's own remarks on why a cleared slot is what the projection BECOMES, never an
            // independent edit made here.
            m_revision++;

            // Revoking Control re-tests every application this principal stands on: the authority to apply and the
            // application are separate storage, so nothing else would ever drop one whose authority has been
            // withdrawn. Re-testing (rather than matching the revoked subject) is what makes a WILDCARD revoke drop
            // the concrete applications it was the only basis for.
            if (capability == WorldCapability.Control) {
                DissolveUnauthorizedApplications(principal: principal);
            }
        }

        return removed;
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
            var exclusive = (m_exclusive.TryGetValue(
                key: new ExclusiveKey(
                    Capability: capability,
                    Subject: subject
                ),
                value: out var holder
            ) && (holder == principal));

            rows.Add(item: new WorldGrant(
                Principal: principal,
                Capability: capability,
                Subject: subject,
                Exclusive: exclusive,
                Budget: (m_budgets.TryGetValue(
                    key: key,
                    value: out var budget
                )
                ? budget
                : null),
                Reach: (m_channelReach.TryGetValue(
                    key: key,
                    value: out var reach
                )
                ? reach
                : null),
                KindMask: (m_kindMasks.TryGetValue(
                    key: key,
                    value: out var kinds
                )
                ? kinds
                : null),
                WriteMask: (m_writeMasks.TryGetValue(
                    key: key,
                    value: out var writes
                )
                ? writes
                : null),
                HoldCeiling: (m_holdCeilings.TryGetValue(
                    key: key,
                    value: out var holdCeiling
                )
                ? holdCeiling
                : null)
            ));
        }

        return rows;
    }
    /// <inheritdoc/>
    public void SetApplications(WorldPrincipal principal, IReadOnlyList<ControlApplication> applications) {
        ArgumentNullException.ThrowIfNull(argument: applications);

        var previous = Applications(principal: principal);
        var composed = new List<ControlApplication>(collection: applications);

        // The default set has exactly one canonical representation — an ABSENT row — so a set composed back to the
        // default never lingers as a stored row that CollectApplicationHolders would then report as a composition.
        if (SameApplications(
            first: composed,
            second: DefaultApplications(principal: principal)
        )) {
            _ = m_applications.Remove(key: principal);
        } else {
            m_applications[principal] = composed;
        }

        foreach (var dropped in previous) {
            if (!Holds(
                applications: composed,
                target: dropped.Target
            )) {
                NotifyApplicationTransition(
                    current: null,
                    previous: dropped.Target,
                    principal: principal
                );
            }
        }

        foreach (var added in composed) {
            if (!Holds(
                applications: previous,
                target: added.Target
            )) {
                NotifyApplicationTransition(
                    current: added.Target,
                    previous: null,
                    principal: principal
                );
            }
        }
    }
    /// <summary>Collects stale generations for one peer index. The caller revokes their rows through
    /// <see cref="WorldServer.Revoke"/>; this method never bypasses that door.</summary>
    /// <param name="index">The peer body index.</param>
    /// <param name="currentGeneration">The newly admitted generation.</param>
    /// <returns>The stale peer identities.</returns>
    public IReadOnlyList<WorldPrincipal> StalePeerGenerations(int index, int currentGeneration) {
        var stale = new List<WorldPrincipal>();

        foreach (var principal in m_byPrincipal.Keys) {
            if (
                (principal.Kind == PrincipalKind.Peer) &&
                (principal.Index == index) &&
                (principal.Generation != currentGeneration)
            ) {
                stale.Add(item: principal);
            }
        }

        stale.Sort(comparison: static (left, right) => left.Generation.CompareTo(value: right.Generation));

        return stale;
    }
    /// <summary>Resyncs the group+membership+ownership index wholesale from the live document's <c>groups</c>
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
            m_groupReach[group.Id] = (reachByKindName.TryGetValue(
                key: group.KindName,
                value: out var reach
            )
                ? reach
                : new HashSet<WorldCapability>()
            );
            groupsById[group.Id] = group;

            foreach (var member in group.Members) {
                if (!m_groupMembership.TryGetValue(
                    key: member,
                    value: out var memberOf
                )) {
                    memberOf = new List<string>();
                    m_groupMembership[member] = memberOf;
                }

                memberOf.Add(item: group.Id);
            }
        }

        // Ownership is not a grant — a fact this door consults (GrantRule.OwnershipHold). Only Subject.Kind Group
        // exists today; a later subject-kind widening adds its own case here.
        foreach (var row in ownership) {
            if (row.Subject.Kind != OwnershipSubjectKind.Group) {
                continue;
            }

            switch (row.Owner.Kind) {
                case OwnershipOwnerKind.Principal:
                    if (row.Owner.Principal is { } ownerPrincipal) {
                        AddOwnedGroup(
                            owner: ownerPrincipal,
                            groupId: row.Subject.Id
                        );
                    }

                    break;
                case OwnershipOwnerKind.Group:
                    // A group owns a group: every CURRENT member of the owning group reaches the SUBJECT group's own
                    // rows too — one level, resolved here against this same pass's roster, never recursively (a
                    // member is never itself a group).
                    if (
                        (row.Owner.GroupId is { } ownerGroupId) &&
                        groupsById.TryGetValue(
                        key: ownerGroupId,
                        value: out var ownerGroup
                    )
                    ) {
                        foreach (var member in ownerGroup.Members) {
                            AddOwnedGroup(
                                owner: member,
                                groupId: row.Subject.Id
                            );
                        }
                    }

                    break;
            }
        }
    }
    /// <summary>Resyncs the drive-admission gate index wholesale from the live document's <c>state</c> section —
    /// called alongside <see cref="SyncGroups"/> at the same choke points (construction, every <c>Install</c>), so a
    /// live <c>world.state.cell.set</c> that flips a gate row's cell is settled before the next tick's intent drain
    /// reads it. Resolves each candidate cell through
    /// <see cref="WorldStateReader.TryRead"/> — the section's one (row, key) read seam — rather than a bespoke scan
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
                if (
                    !int.TryParse(
                    s: cell.Key,
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var bodyIndex
                ) ||
                    (bodyIndex < 0) ||
                    m_driveGates.ContainsKey(key: bodyIndex)
                ) {
                    continue;
                }

                if (
                    WorldStateReader.TryRead(
                    definition: definition,
                    rowName: row.Name,
                    key: cell.Key.Value,
                    tick: 0UL,
                    row: out _,
                    rawValue: out var raw,
                    text: out _
                ) &&
                    (raw is { } value) &&
                    (value != 0)
                ) {
                    m_driveGates[bodyIndex] = row.Name;
                }
            }
        }
    }
    /// <inheritdoc/>
    public bool TryGetBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget) =>
        m_budgets.TryGetValue(
            key: (principal, capability, subject),
            value: out budget
        );
    /// <inheritdoc/>
    public bool TryGetChannelReach(WorldPrincipal principal, GrantSubject subject, out ChannelReachMask mask) =>
        m_channelReach.TryGetValue(
            key: (principal, WorldCapability.Drive, subject),
            value: out mask
        );
    /// <summary>Determines whether <paramref name="bodyIndex"/> is currently drive-gated — carries a nonzero cell on a state row
    /// declaring <see cref="WorldStateRow.GatesDrive"/> — and, when it is, which row decided it. Checked fresh every
    /// call against the index <see cref="SyncState"/> last resynced; never latched.</summary>
    /// <param name="bodyIndex">The 0-based entity index to check.</param>
    /// <param name="gateRow">The deciding row's name, when gated; empty otherwise.</param>
    /// <returns><see langword="true"/> when the body is gated.</returns>
    public bool TryGetDriveGate(int bodyIndex, out string gateRow) {
        if (m_driveGates.TryGetValue(
            key: bodyIndex,
            value: out var found
        )) {
            gateRow = found;

            return true;
        }

        gateRow = string.Empty;

        return false;
    }
    /// <inheritdoc/>
    public bool TryGetEventBudget(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out ushort budget) =>
        m_eventBudgets.TryGetValue(
            key: (principal, capability, subject),
            value: out budget
        );
    /// <inheritdoc/>
    public bool TryGetKindMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out MutationKindMask mask) =>
        m_kindMasks.TryGetValue(
            key: (principal, capability, subject),
            value: out mask
        );
    /// <inheritdoc/>
    public bool TryGetWriteMask(WorldPrincipal principal, WorldCapability capability, GrantSubject subject, out DocumentWriteMask mask) =>
        m_writeMasks.TryGetValue(
            key: (principal, capability, subject),
            value: out mask
        );
    /// <summary>Adds a grant, enforcing exclusivity in both orders. An incoming exclusive grant over the wildcard
    /// <see cref="GrantSubject.All"/> is rejected outright (an exclusive reservation must name a concrete subject). A
    /// grant is rejected outright when its subject is not one its capability legitimately admits (see
    /// <see cref="IsLegitimateSubject"/> for the full per-capability table and why): a concrete subject in the
    /// capability's own domain (a body for Drive, a screen for Control, a section for Mutate, a state row for Edit) is
    /// legitimate for any principal, while the wildcard is legitimate only for a principal already inside the local
    /// trust boundary for that capability (generally Console/Seat; Peer additionally for Control, per its own boot
    /// seed). A Drive grant's concrete <see cref="GrantSubjectKind.Body"/> is additionally bounded to an index the
    /// population actually holds. The grant is rejected when a different principal already holds a conflicting
    /// exclusive reservation of an overlapping subject (whether the incoming grant is exclusive or ordinary), or when
    /// an incoming exclusive grant would share the same concrete subject with a different principal's ordinary hold.
    /// The wildcard <see cref="GrantSubject.All"/> ordinary grant is exempt from the exclusivity-conflict check in
    /// both directions — an existing exclusive concrete hold never blocks a later ordinary wildcard re-grant, and the
    /// seeded wildcard never blocks a later exclusive concrete acquisition — so the permissive local defaults can
    /// always be narrowed and re-widened regardless of what exclusive holds exist elsewhere; enforcement
    /// (<see cref="Allows"/>) makes the exclusive holder the sole effective owner of its own subject regardless.
    /// Re-granting a subject the same principal already holds is idempotent (an upgrade to exclusive still records the
    /// reservation). Not part of <see cref="IWorldGrantsView"/>: this is an authority door, reached only through
    /// <see cref="WorldServer.Grant"/>'s <see cref="HoldsForAdministration"/> actor check, never through the view a
    /// non-<see cref="WorldServer"/> caller holds.</summary>
    /// <param name="grant">The grant to add.</param>
    /// <param name="reason">On rejection, the human-readable reason; empty on success.</param>
    /// <returns><see langword="true"/> when the grant was added.</returns>
    public bool TryGrant(WorldGrant grant, out string reason) {
        if (Conflicts(
            grant: grant,
            reason: out reason
        )) {
            return false;
        }

        if (grant.Exclusive) {
            m_exclusive[new ExclusiveKey(
                Capability: grant.Capability,
                Subject: grant.Subject
            )] = grant.Principal;
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

        if (
            (grant.Consent is { } consent) &&
            (grant.Ceiling is { } authoredCeiling)
        ) {
            // The SEAT'S OWN ceiling gesture (Conflicts already refused this pair anywhere else): write the number
            // onto exactly the ordinals the mask names and leave every other ordinal as it was, so a second gesture
            // can give `turn` a different ceiling than `forward` without erasing the first. Revoke clears the value.
            ref var ceilings = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                dictionary: m_poolCeilings,
                exists: out _,
                key: key
            );

            ceilings = ceilings.WithCeiling(
                ceiling: authoredCeiling,
                channels: consent
            );
        } else if (grant.Reach is { } reach) {
            // A contributor's REACH, last-write-wins exactly like a budget re-grant: a re-grant naming a different
            // channel set IS the reach-update verb, no new grammar needed.
            m_channelReach[key] = reach;
        }

        if (grant.KindMask is { } kindMask) {
            // Unconditional, last-write-wins — the same shape as m_budgets/m_channelReach above.
            m_kindMasks[key] = kindMask;
        } else if (CarriesKindMask(
            capability: grant.Capability,
            subject: grant.Subject
        )) {
            // THE ONE ASYMMETRY: WorldGrant.KindMask's own doc names it — a re-grant of a maskable row that carries
            // NO mask CLEARS a previously-recorded one, rather than leaving it untouched the way an omitted
            // Budget/Reach does. A mask a re-grant does not repeat is a mask the operator meant to take back;
            // silently surviving a re-grant that dropped it would be the opposite of what the operator typed.
            _ = m_kindMasks.Remove(key: key);
        }

        if (grant.WriteMask is { } writeMask) {
            m_writeMasks[key] = writeMask;
        } else if (CarriesWriteMask(
            capability: grant.Capability,
            subject: grant.Subject
        )) {
            _ = m_writeMasks.Remove(key: key);
        }

        ref var grants = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_byPrincipal,
            key: grant.Principal,
            exists: out _
        );

        grants.Add(
            capability: grant.Capability,
            subject: grant.Subject
        );
        m_revision++;

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Bumped by every mutator that changes a principal's held subject sets — <see cref="TryGrant"/>, a
    /// removing <see cref="Revoke"/>, and the engagement-route helpers below — never by a read.</remarks>
    public int Revision => m_revision;

    /// <summary>One principal's checkpointed capability rows — the five subject sets plus the route policy payload.
    /// Excludes <see cref="m_handleTables"/>: a per-(principal, capability) handle table is a pure projection of
    /// this table, re-derived lazily from <see cref="ProjectSubjects"/> the moment its own revision cache goes
    /// stale, and every live handle a guest could hold is meaningless the instant that guest's own connection drops
    /// — which every checkpoint restart already forces (the arm gate refuses a checkpoint of a server any addon has
    /// ever pumped, and a remote human is parked, not left connected, across a restore) — the same "subscribers
    /// re-attach" exclusion <see cref="WorldOutputHub"/>/<see cref="WorldTcpHost"/> connections already carry.</summary>
    public sealed record WorldGrantsPrincipalCheckpoint(
        WorldPrincipal Principal,
        IReadOnlyList<GrantSubject> Drive,
        IReadOnlyList<GrantSubject> Observe,
        IReadOnlyList<GrantSubject> Control,
        IReadOnlyList<GrantSubject> Mutate,
        IReadOnlyList<GrantSubject> Edit,
        IReadOnlyList<ControlApplication> Applications
    );
    /// <summary>The grant table's own checkpointed state — every table this class owns.</summary>
    public sealed record WorldGrantsCheckpoint(
        IReadOnlyList<WorldGrantsPrincipalCheckpoint> Principals,
        IReadOnlyList<(WorldCapability Capability, GrantSubject Subject, WorldPrincipal Holder)> Exclusive,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, ushort Budget)> Budgets,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, ushort Budget)> EventBudgets,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, long Ceiling)> HoldCeilings,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, ulong Bits)> ChannelReach,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, IReadOnlyList<(int Ordinal, long Ceiling)> Ceilings)> PoolCeilings,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, UInt128 Bits)> KindMasks,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, ulong Bits)> WriteMasks,
        IReadOnlyList<(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject)> SeededSections,
        IReadOnlyList<(WorldPrincipal Principal, IReadOnlyList<string> Groups)> GroupMembership,
        IReadOnlyList<(string Group, IReadOnlyList<WorldCapability> Reach)> GroupReach,
        IReadOnlyList<(WorldPrincipal Principal, IReadOnlyList<string> Groups)> OwnedGroups,
        IReadOnlyList<(int BodyIndex, string Reason)> DriveGates,
        int Revision
    );

    /// <summary>Captures every table this class owns.</summary>
    public WorldGrantsCheckpoint Capture() {
        var principals = new List<WorldGrantsPrincipalCheckpoint>(capacity: m_byPrincipal.Count);

        foreach (var (principal, grants) in m_byPrincipal) {
            principals.Add(item: new WorldGrantsPrincipalCheckpoint(
                Principal: principal,
                Drive: [.. (grants.For(capability: WorldCapability.Drive) ?? [])],
                Observe: [.. (grants.For(capability: WorldCapability.Observe) ?? [])],
                Control: [.. (grants.For(capability: WorldCapability.Control) ?? [])],
                Mutate: [.. (grants.For(capability: WorldCapability.Mutate) ?? [])],
                Edit: [.. (grants.For(capability: WorldCapability.Edit) ?? [])],
                Applications: [.. (m_applications.GetValueOrDefault(key: principal) ?? [])]
            ));
        }

        // A principal may have composed an application set without holding any capability row of its own, so the
        // application table is swept separately rather than assumed to be a subset of the capability table.
        foreach (var (principal, applications) in m_applications) {
            if (!m_byPrincipal.ContainsKey(key: principal)) {
                principals.Add(item: new WorldGrantsPrincipalCheckpoint(
                    Principal: principal,
                    Drive: [],
                    Observe: [],
                    Control: [],
                    Mutate: [],
                    Edit: [],
                    Applications: [.. applications]
                ));
            }
        }

        return new WorldGrantsCheckpoint(
            Principals: principals,
            Exclusive: [.. m_exclusive.Select(selector: static pair => (pair.Key.Capability, pair.Key.Subject, pair.Value))],
            Budgets: [.. m_budgets.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            EventBudgets: [.. m_eventBudgets.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            HoldCeilings: [.. m_holdCeilings.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            ChannelReach: [.. m_channelReach.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            PoolCeilings: [.. m_poolCeilings.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, CaptureCeilings(ceilings: pair.Value)))],
            KindMasks: [.. m_kindMasks.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            WriteMasks: [.. m_writeMasks.Select(selector: static pair => (pair.Key.Principal, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            SeededSections: [.. m_seededSections.Select(selector: static key => (key.Principal, key.Capability, key.Subject))],
            GroupMembership: [.. m_groupMembership.Select(selector: static pair => (pair.Key, ((IReadOnlyList<string>)[.. pair.Value])))],
            GroupReach: [.. m_groupReach.Select(selector: static pair => (pair.Key, ((IReadOnlyList<WorldCapability>)[.. pair.Value])))],
            OwnedGroups: [.. m_ownedGroups.Select(selector: static pair => (pair.Key, ((IReadOnlyList<string>)[.. pair.Value])))],
            DriveGates: [.. m_driveGates.Select(selector: static pair => (pair.Key, pair.Value))],
            Revision: m_revision
        );
    }
    /// <summary>Restores every table this class owns from a previously captured checkpoint — a wholesale replace,
    /// never a merge onto whatever the boot-seed constructor already installed.</summary>
    public void Restore(WorldGrantsCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        m_applications.Clear();
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
        m_groupMembership.Clear();
        m_groupReach.Clear();
        m_ownedGroups.Clear();
        m_driveGates.Clear();

        foreach (var row in checkpoint.Principals) {
            var grants = new PrincipalGrants();

            foreach (var subject in row.Drive) {
                grants.Add(capability: WorldCapability.Drive, subject: subject);
            }
            foreach (var subject in row.Observe) {
                grants.Add(capability: WorldCapability.Observe, subject: subject);
            }
            foreach (var subject in row.Control) {
                grants.Add(capability: WorldCapability.Control, subject: subject);
            }
            foreach (var subject in row.Mutate) {
                grants.Add(capability: WorldCapability.Mutate, subject: subject);
            }
            foreach (var subject in row.Edit) {
                grants.Add(capability: WorldCapability.Edit, subject: subject);
            }

            m_byPrincipal[row.Principal] = grants;

            if (row.Applications.Count > 0) {
                m_applications[row.Principal] = [.. row.Applications];
            }
        }

        foreach (var row in checkpoint.Exclusive) {
            m_exclusive[new ExclusiveKey(
                Capability: row.Capability,
                Subject: row.Subject
            )] = row.Holder;
        }
        foreach (var row in checkpoint.Budgets) {
            m_budgets[(row.Principal, row.Capability, row.Subject)] = row.Budget;
        }
        foreach (var row in checkpoint.EventBudgets) {
            m_eventBudgets[(row.Principal, row.Capability, row.Subject)] = row.Budget;
        }
        foreach (var row in checkpoint.HoldCeilings) {
            m_holdCeilings[(row.Principal, row.Capability, row.Subject)] = row.Ceiling;
        }
        foreach (var row in checkpoint.ChannelReach) {
            m_channelReach[(row.Principal, row.Capability, row.Subject)] = new ChannelReachMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.PoolCeilings) {
            m_poolCeilings[(row.Principal, row.Capability, row.Subject)] = RestoreCeilings(rows: row.Ceilings);
        }
        foreach (var row in checkpoint.KindMasks) {
            m_kindMasks[(row.Principal, row.Capability, row.Subject)] = new MutationKindMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.WriteMasks) {
            m_writeMasks[(row.Principal, row.Capability, row.Subject)] = new DocumentWriteMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.SeededSections) {
            _ = m_seededSections.Add(item: new SeededKey(
                Capability: row.Capability,
                Principal: row.Principal,
                Subject: row.Subject
            ));
        }
        foreach (var row in checkpoint.GroupMembership) {
            m_groupMembership[row.Principal] = [.. row.Groups];
        }
        foreach (var row in checkpoint.GroupReach) {
            m_groupReach[row.Group] = [.. row.Reach];
        }
        foreach (var row in checkpoint.OwnedGroups) {
            m_ownedGroups[row.Principal] = [.. row.Groups];
        }
        foreach (var row in checkpoint.DriveGates) {
            m_driveGates[row.BodyIndex] = row.Reason;
        }

        m_revision = checkpoint.Revision;
    }

    private static IReadOnlyList<(int Ordinal, long Ceiling)> CaptureCeilings(ChannelCeilings ceilings) {
        var rows = new List<(int, long)>();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if ((ceilings.Support.Bits & (1UL << ordinal)) != 0UL) {
                rows.Add(item: (ordinal, ceilings[ordinal]));
            }
        }

        return rows;
    }
    private static ChannelCeilings RestoreCeilings(IReadOnlyList<(int Ordinal, long Ceiling)> rows) {
        var ceilings = default(ChannelCeilings);

        foreach (var row in rows) {
            ceilings = ceilings.WithCeiling(
                ceiling: row.Ceiling,
                channels: new ChannelConsentMask(Bits: (1UL << row.Ordinal))
            );
        }

        return ceilings;
    }

    // One principal's five per-capability subject sets, allocated lazily. A struct held by ref in the dictionary; the
    // sets are reference types so ref-mutation persists.
    private struct PrincipalGrants {
        private HashSet<GrantSubject>? m_drive;
        private HashSet<GrantSubject>? m_observe;
        private HashSet<GrantSubject>? m_control;
        private HashSet<GrantSubject>? m_mutate;
        private HashSet<GrantSubject>? m_edit;

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
                    throw new ArgumentOutOfRangeException(
                        paramName: nameof(capability),
                        actualValue: capability,
                        message: $"WorldCapability.{capability} has no storage arm in PrincipalGrants.Set — add a field and a case here before granting it."
                    );
            }
        }

        public void Add(WorldCapability capability, GrantSubject subject) {
            _ = Set(capability: capability).Add(item: subject);
        }
        // Exhaustive over WorldCapability's five declared members only — a future member has no storage field to
        // fall back to, so this throws rather than silently sharing m_edit's slot. This is defense-in-depth, not a
        // live gate: every data path is filtered by IsLegitimateSubject or a closed parse before storage is
        // consulted, and Allows short-circuits on the m_byPrincipal miss first.
        public readonly HashSet<GrantSubject>? For(WorldCapability capability) => capability switch {
            WorldCapability.Drive => m_drive,
            WorldCapability.Observe => m_observe,
            WorldCapability.Control => m_control,
            WorldCapability.Mutate => m_mutate,
            WorldCapability.Edit => m_edit,
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(capability),
            actualValue: capability,
            message: $"WorldCapability.{capability} has no storage arm in PrincipalGrants.For — add a field and a case here before granting it."
        ),
        };
        public readonly bool Remove(WorldCapability capability, GrantSubject subject) {
            return (For(capability: capability)?.Remove(item: subject) ?? false);
        }
    }
    // The reverse-index key for the exclusive-holder table.
    private readonly record struct ExclusiveKey(WorldCapability Capability, GrantSubject Subject);
    // The seed-marker key: one permissive-default row as constructed (principal + capability + subject).
    private readonly record struct SeededKey(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject);
}
