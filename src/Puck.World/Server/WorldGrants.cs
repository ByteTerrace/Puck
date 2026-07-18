using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The server's ONE capability table — the single primitive the three former ad-hoc ownership forms (the engagement
/// latch, machine-input ownership, the addon slot owner) unify into: a set of <c>(principal, capability, subject)</c>
/// grants, seeded permissive for local play and mutated live through <c>world.grant</c>/<c>world.revoke</c>. Every
/// write boundary asks <see cref="Allows"/> before it acts; <see cref="WorldEngagement"/> is a VIEW over the
/// <see cref="WorldCapability.Control"/> screen routes here, not a parallel table.
/// </summary>
/// <remarks>
/// <para>Storage is a per-principal record of four subject sets (one per capability), so a per-tick
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
/// wildcard <see cref="GrantSubject.All"/> grant is DELIBERATELY EXEMPT on the ordinary
/// side: the permissive local defaults seed the console with <c>Drive/all</c> and seats/peers with <c>Control/all</c>,
/// and that backdrop must never block a principal (e.g. an addon) from taking an exclusive hold on one specific body —
/// so <c>world.grant addon:x drive body:n exclusive</c> succeeds even though the console holds <c>Drive/all</c>.</description></item>
/// <item><description><b>Enforcement (<see cref="Allows"/>).</b> Once <c>body:n</c> is exclusively reserved by principal
/// P, <see cref="Allows"/> answers TRUE only for P — the exclusive holder OVERRIDES every other grant, INCLUDING the
/// permissive <c>Drive/all</c> wildcard. So the exempt backdrop from acquisition cannot actually drive an exclusively
/// held body: exclusivity, not acquisition-time blocking, is what makes the reservation exclusive at the intent
/// boundary. When a subject is not exclusively reserved, the normal wildcard/subject-set logic applies unchanged.</description></item>
/// </list>
/// <para>Single-threaded, like every server type here: grants apply in the command-apply window and are read at the
/// tick boundary, both on the launcher's window-pump thread. No lock guards this state.</para>
/// </remarks>
internal sealed class WorldGrants {
    private readonly Dictionary<WorldPrincipal, PrincipalGrants> m_byPrincipal = new();
    // (capability, subject) -> the exclusive holder. Guards double-exclusive acquisition (the engagement latch's
    // "a live holder owns it" rule, generalized). Only exclusive grants appear here.
    private readonly Dictionary<ExclusiveKey, WorldPrincipal> m_exclusive = new();

    /// <summary>Seeds the permissive local-play defaults so boot behavior is UNCHANGED until someone revokes: every seat
    /// holds Drive over its own body and Control/Mutate/Edit over its domain; the console holds Drive over any body and
    /// Control/Mutate/Edit over its domain; every population peer holds Control over any screen (population entries engage
    /// diegetic machines today, exactly like seats — the route capability, not Drive: peers do not submit intents).
    /// Addons get nothing. Mutate is seeded per-section (not the wildcard) so a single section can be revoked; Drive,
    /// Control, and Edit use the wildcard.</summary>
    /// <param name="seatCount">The reserved local-seat count (each seat 0..seatCount-1 gets its default body grant).</param>
    /// <param name="population">The entity-table ceiling (peers seatCount..population-1 get default Control).</param>
    public WorldGrants(int seatCount, int population) {
        for (var slot = 0; (slot < seatCount); slot++) {
            var seat = WorldPrincipal.Seat(slot: slot);

            _ = TryGrant(grant: new WorldGrant(Principal: seat, Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: slot), Exclusive: false), reason: out _);
            SeedDomain(principal: seat);
        }

        SeedDomain(principal: WorldPrincipal.Console);
        _ = TryGrant(grant: new WorldGrant(Principal: WorldPrincipal.Console, Capability: WorldCapability.Drive, Subject: GrantSubject.All, Exclusive: false), reason: out _);

        // Population peers can engage a diegetic screen (a network human's route) — seed the Control capability so the
        // grant-backed engagement view behaves exactly as the former index-only route table did.
        for (var index = seatCount; (index < population); index++) {
            _ = TryGrant(grant: new WorldGrant(Principal: WorldPrincipal.Peer(index: index), Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false), reason: out _);
        }
    }

    // Control over every screen, Mutate over every section (per-section, so one is revocable), Edit over every profile
    // section — the non-Drive permissive defaults shared by seats and the console.
    private void SeedDomain(WorldPrincipal principal) {
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false), reason: out _);
        _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Edit, Subject: GrantSubject.All, Exclusive: false), reason: out _);

        foreach (var section in Enum.GetValues<WorldSection>()) {
            _ = TryGrant(grant: new WorldGrant(Principal: principal, Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section: section), Exclusive: false), reason: out _);
        }
    }

    /// <summary>Whether <paramref name="principal"/> holds <paramref name="capability"/> over <paramref name="subject"/>
    /// — the allocation-free, O(1) hot-path check. When the subject is exclusively reserved, ONLY the reserver is
    /// allowed (the exclusivity override — the exclusive holder beats every other grant, including the wildcard).
    /// Otherwise it is true when the principal's subject set for the capability holds the subject or the
    /// <see cref="GrantSubject.All"/> wildcard.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to test.</param>
    /// <param name="subject">The subject to test.</param>
    public bool Allows(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        // The exclusivity override: a reserved subject answers for its reserver ALONE, so an exclusively-held body has
        // exactly one effective driver even though the console still holds the seeded Drive/all wildcard (§CR-1).
        if (ExclusiveHolderOf(capability: capability, subject: subject) is { } holder) {
            return (holder == principal);
        }

        if (!m_byPrincipal.TryGetValue(key: principal, value: out var grants)) {
            return false;
        }

        var subjects = grants.For(capability: capability);

        return (subjects is not null) && (subjects.Contains(item: GrantSubject.All) || subjects.Contains(item: subject));
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

    /// <summary>Whether <paramref name="principal"/> holds <paramref name="capability"/> over EVERY
    /// <see cref="WorldSection"/> — the check a whole-document swap or journal undo passes (it can touch any section).</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to test (today <see cref="WorldCapability.Mutate"/>).</param>
    public bool AllowsAllSections(WorldPrincipal principal, WorldCapability capability) {
        foreach (var section in Enum.GetValues<WorldSection>()) {
            if (!Allows(principal: principal, capability: capability, subject: GrantSubject.Section(section: section))) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Adds a grant, enforcing exclusivity in BOTH orders (§CR-1). An incoming EXCLUSIVE grant over the wildcard
    /// <see cref="GrantSubject.All"/> is rejected outright (an exclusive reservation must name a concrete subject). The
    /// grant is REJECTED when a DIFFERENT principal already holds a conflicting exclusive reservation of an overlapping
    /// subject (whether the incoming grant is exclusive or ordinary), or when an incoming EXCLUSIVE grant would share the
    /// same concrete subject with a different principal's ordinary hold. The wildcard <see cref="GrantSubject.All"/>
    /// ordinary grant is exempt on the ordinary
    /// side, so the permissive local defaults never block an exclusive acquisition; enforcement (<see cref="Allows"/>)
    /// makes the exclusive holder the sole effective owner. Re-granting a subject the SAME principal already holds is
    /// idempotent (an upgrade to exclusive still records the reservation).</summary>
    /// <param name="grant">The grant to add.</param>
    /// <param name="reason">On rejection, the human-readable reason; empty on success.</param>
    /// <returns><see langword="true"/> when the grant was added.</returns>
    public bool TryGrant(WorldGrant grant, out string reason) {
        if (Conflicts(grant: grant, reason: out reason)) {
            return false;
        }

        if (grant.Exclusive) {
            m_exclusive[new ExclusiveKey(Capability: grant.Capability, Subject: grant.Subject)] = grant.Principal;
        }

        ref var grants = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_byPrincipal, key: grant.Principal, exists: out _);

        grants.Add(capability: grant.Capability, subject: grant.Subject);

        return true;
    }

    // Whether an incoming grant conflicts with an existing hold under the §CR-1 exclusivity rule. Grant/revoke is a
    // human-cadence op (never the tick path), so the two scans are affordable; both are skipped entirely for the common
    // idempotent re-grant (a matching holder is the incoming principal itself).
    private bool Conflicts(WorldGrant grant, out string reason) {
        reason = string.Empty;

        // (0) An exclusive reservation MUST name a concrete subject. An "exclusively own everything" claim (exclusive
        //     `all`) has no legitimate consumer today and is order-dependently dishonest: on a table with a prior
        //     concrete hold the reverse order rejects, while this order slips past acquisition and then denies EVERY
        //     concrete seat at enforcement. Reject it outright — in BOTH orders and on a fresh table — so an exclusive
        //     hold always means one named subject. The ordinary `all` wildcard (the permissive backdrop) is untouched.
        if (grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All)) {
            reason = $"an exclusive {Label(capability: grant.Capability)} reservation must name a concrete subject (exclusive 'all' is not allowed)";

            return true;
        }

        // (1) A DIFFERENT principal's exclusive reservation of an overlapping subject blocks EITHER order — an incoming
        //     exclusive collides with it, and an incoming ordinary grant would step onto a subject someone reserved.
        foreach (var pair in m_exclusive) {
            if ((pair.Key.Capability == grant.Capability) && (pair.Value != grant.Principal) && SubjectsOverlap(a: grant.Subject, b: pair.Key.Subject)) {
                reason = $"{grant.Subject.Describe()} conflicts with {pair.Value.Describe()}'s exclusive {Label(capability: grant.Capability)} {pair.Key.Subject.Describe()}";

                return true;
            }
        }

        // (2) An incoming EXCLUSIVE grant additionally rejects when a DIFFERENT principal already holds the SAME concrete
        //     subject ordinarily (the ordinary-then-exclusive order). The wildcard `all` is exempt: a Contains of a
        //     concrete subject never matches an `all`-only set, so the seeded Drive/all backdrop does not block here.
        if (grant.Exclusive && (grant.Subject.Kind != GrantSubjectKind.All)) {
            foreach (var pair in m_byPrincipal) {
                if ((pair.Key != grant.Principal) && (pair.Value.For(capability: grant.Capability)?.Contains(item: grant.Subject) == true)) {
                    reason = $"{grant.Subject.Describe()} is already held by {pair.Key.Describe()}";

                    return true;
                }
            }
        }

        return false;
    }

    // Two subjects overlap when they are identical or either is the `all` wildcard (which covers every concrete subject
    // of its capability). Used only for exclusive-reservation conflicts — the ordinary wildcard backdrop is exempt and
    // checked separately.
    private static bool SubjectsOverlap(GrantSubject a, GrantSubject b) {
        return (a == b) || (a.Kind == GrantSubjectKind.All) || (b.Kind == GrantSubjectKind.All);
    }

    private static string Label(WorldCapability capability) => capability.ToString().ToLowerInvariant();

    /// <summary>Removes a grant (capability+subject) from a principal, and clears any matching exclusive reservation.
    /// A no-op that returns <see langword="false"/> when the principal did not hold it.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="capability">The capability to revoke.</param>
    /// <param name="subject">The subject to revoke.</param>
    /// <returns>Whether a grant was actually removed.</returns>
    public bool Revoke(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) {
        var removed = false;

        if (m_byPrincipal.TryGetValue(key: principal, value: out var grants)) {
            removed = grants.Remove(capability: capability, subject: subject);
        }

        var key = new ExclusiveKey(Capability: capability, Subject: subject);

        if (m_exclusive.TryGetValue(key: key, value: out var holder) && (holder == principal)) {
            _ = m_exclusive.Remove(key: key);
        }

        return removed;
    }

    /// <summary>The 0-based entity index of the first body a principal holds a Drive grant over (exclusive or not) — the
    /// addon driver's body binding, discovered from the grant table (the grant IS the assignment). <see langword="null"/>
    /// when the principal drives no specific body.</summary>
    /// <param name="principal">The acting identity.</param>
    public int? FirstDriveBody(WorldPrincipal principal) {
        return (m_byPrincipal.TryGetValue(key: principal, value: out var grants) ? grants.FirstBody(capability: WorldCapability.Drive) : null);
    }

    /// <summary>Repoints a principal's engagement route to <paramref name="screenIndex"/> — the engagement latch, stored
    /// as the ONE table's Control state: drops any prior screen route the principal held (a re-engage) and records the
    /// new one. The permission to engage (a Control grant over the screen or the wildcard) is checked separately by the
    /// caller; this only records the resolved route.</summary>
    /// <param name="principal">The engaged principal.</param>
    /// <param name="screenIndex">The engine screen index engaged.</param>
    public void SetControlRoute(WorldPrincipal principal, int screenIndex) {
        ref var grants = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_byPrincipal, key: principal, exists: out _);

        grants.ClearScreenRoutes();
        grants.Add(capability: WorldCapability.Control, subject: GrantSubject.Screen(index: screenIndex));
    }

    /// <summary>Clears a principal's engagement route (every Control screen subject it holds) — the disengage half.
    /// Returns whether the principal had a route.</summary>
    /// <param name="principal">The principal to disengage.</param>
    public bool ClearControlRoute(WorldPrincipal principal) {
        return m_byPrincipal.TryGetValue(key: principal, value: out var grants) && grants.ClearScreenRoutes();
    }

    /// <summary>The engine screen index a principal is engaged on (its single Control screen route), or
    /// <see langword="null"/>.</summary>
    /// <param name="principal">The principal.</param>
    public int? ControlRoute(WorldPrincipal principal) {
        return (m_byPrincipal.TryGetValue(key: principal, value: out var grants) ? grants.ScreenRoute() : null);
    }

    /// <summary>Collects every principal engaged on <paramref name="screenIndex"/> into <paramref name="into"/> (cleared
    /// first) — the multiplayer-cabinet merge set. Allocation-free with a reused list: iterates the concrete
    /// dictionaries with struct enumerators.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <param name="into">The reusable destination list.</param>
    public void CollectRouteHolders(int screenIndex, List<WorldPrincipal> into) {
        into.Clear();

        var subject = GrantSubject.Screen(index: screenIndex);

        foreach (var pair in m_byPrincipal) {
            if (pair.Value.HoldsControlScreen(subject: subject)) {
                into.Add(item: pair.Key);
            }
        }
    }

    /// <summary>Renders the grant table for the <c>world.grants</c> echo — one bracketed segment per principal, or one
    /// principal's rows when <paramref name="filter"/> is set. Diagnostics only; not on any tick path.</summary>
    /// <param name="filter">A single principal to describe, or <see langword="null"/> for the whole table.</param>
    /// <returns>The echo string.</returns>
    public string Describe(WorldPrincipal? filter) {
        var builder = new StringBuilder(value: "[world.grants:");
        var any = false;

        foreach (var pair in m_byPrincipal) {
            if ((filter is { } only) && (pair.Key != only)) {
                continue;
            }

            var rows = pair.Value.Describe(exclusiveOf: pair.Key, exclusive: m_exclusive);

            if (rows.Length == 0) {
                continue;
            }

            _ = builder.Append(value: any ? " | " : " ").Append(value: pair.Key.Describe()).Append(value: ' ').Append(value: rows);
            any = true;
        }

        if (!any) {
            _ = builder.Append(value: " (none)");
        }

        return builder.Append(value: ']').ToString();
    }

    // One principal's four per-capability subject sets, allocated lazily. A struct held by ref in the dictionary; the
    // sets are reference types so ref-mutation persists.
    private struct PrincipalGrants {
        private HashSet<GrantSubject>? m_drive;
        private HashSet<GrantSubject>? m_control;
        private HashSet<GrantSubject>? m_mutate;
        private HashSet<GrantSubject>? m_edit;

        public readonly HashSet<GrantSubject>? For(WorldCapability capability) => capability switch {
            WorldCapability.Drive => m_drive,
            WorldCapability.Control => m_control,
            WorldCapability.Mutate => m_mutate,
            _ => m_edit,
        };

        public void Add(WorldCapability capability, GrantSubject subject) {
            _ = Set(capability: capability).Add(item: subject);
        }

        public readonly bool Remove(WorldCapability capability, GrantSubject subject) {
            return (For(capability: capability)?.Remove(item: subject) ?? false);
        }

        public readonly bool HoldsControlScreen(GrantSubject subject) {
            return (m_control?.Contains(item: subject) ?? false);
        }

        public readonly int? FirstBody(WorldCapability capability) {
            if (For(capability: capability) is { } subjects) {
                foreach (var subject in subjects) {
                    if (subject.Kind == GrantSubjectKind.Body) {
                        return subject.Value;
                    }
                }
            }

            return null;
        }

        public readonly int? ScreenRoute() {
            if (m_control is { } control) {
                foreach (var subject in control) {
                    if (subject.Kind == GrantSubjectKind.Screen) {
                        return subject.Value;
                    }
                }
            }

            return null;
        }

        public readonly bool ClearScreenRoutes() {
            if (m_control is not { } control) {
                return false;
            }

            var removed = control.RemoveWhere(match: static subject => (subject.Kind == GrantSubjectKind.Screen));

            return (removed > 0);
        }

        public readonly string Describe(WorldPrincipal exclusiveOf, Dictionary<ExclusiveKey, WorldPrincipal> exclusive) {
            var builder = new StringBuilder();

            Append(builder: builder, capability: WorldCapability.Drive, subjects: m_drive, principal: exclusiveOf, exclusive: exclusive);
            Append(builder: builder, capability: WorldCapability.Control, subjects: m_control, principal: exclusiveOf, exclusive: exclusive);
            Append(builder: builder, capability: WorldCapability.Mutate, subjects: m_mutate, principal: exclusiveOf, exclusive: exclusive);
            Append(builder: builder, capability: WorldCapability.Edit, subjects: m_edit, principal: exclusiveOf, exclusive: exclusive);

            return builder.ToString();
        }

        private static void Append(StringBuilder builder, WorldCapability capability, HashSet<GrantSubject>? subjects, WorldPrincipal principal, Dictionary<ExclusiveKey, WorldPrincipal> exclusive) {
            if ((subjects is null) || (subjects.Count == 0)) {
                return;
            }

            foreach (var subject in subjects) {
                var isExclusive = exclusive.TryGetValue(key: new ExclusiveKey(Capability: capability, Subject: subject), value: out var holder) && (holder == principal);

                if (builder.Length > 0) {
                    _ = builder.Append(value: ' ');
                }

                _ = builder.Append(value: capability.ToString().ToLowerInvariant()).Append(value: '/').Append(value: subject.Describe());

                if (isExclusive) {
                    _ = builder.Append(value: "(x)");
                }
            }
        }

        private HashSet<GrantSubject> Set(WorldCapability capability) {
            switch (capability) {
                case WorldCapability.Drive:
                    return (m_drive ??= new HashSet<GrantSubject>());
                case WorldCapability.Control:
                    return (m_control ??= new HashSet<GrantSubject>());
                case WorldCapability.Mutate:
                    return (m_mutate ??= new HashSet<GrantSubject>());
                default:
                    return (m_edit ??= new HashSet<GrantSubject>());
            }
        }
    }

    // The reverse-index key for the exclusive-holder table.
    private readonly record struct ExclusiveKey(WorldCapability Capability, GrantSubject Subject);
}
