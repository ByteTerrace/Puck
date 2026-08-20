using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The ordinary public door intentionally remains void: callers submit an authority operation and observe its
    // attributed echo. Admission re-authorization additionally needs to know whether the row ACTUALLY reached the
    // live table so a conflict refusal is not later misclassified as an explicit revoke; it uses this identical
    // implementation and keeps the boolean inside the server.
    private bool TryApplyGrant(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        if (!m_grants.HoldsForAdministration(
            principal: actor,
            capability: grant.Capability,
            subject: grant.Subject
        )) {
            DenyGrantTable(
                denial: $"{actor.Describe()} cannot grant {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()} to {grant.Principal.Describe()} — it holds none there itself",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return false;
        }

        if (
            (grant.Capability == WorldCapability.Drive) &&
            (grant.Subject.Kind == GrantSubjectKind.Body) &&
            m_population.IsAdmittedPeer(bodyIndex: grant.Subject.Value) &&
            (grant.Principal != m_population.PeerPrincipal(index: grant.Subject.Value))
        ) {
            DenyGrantTable(
                denial: $"{grant.Principal.Describe()} cannot co-drive {grant.Subject.Describe()} — no consent authorship exists for a remote-admitted body except its own peer ({m_population.PeerPrincipal(index: grant.Subject.Value).Describe()}); Reach ∧ Consent composes to nothing until that peer authors it",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return false;
        }

        if (m_grants.TryGrant(
            grant: grant,
            reason: out var reason
        )) {
            Console.Error.WriteLine(value: $"[world.grant: {label}{(grant.Exclusive
                ? " exclusive"
                : string.Empty)}]");

            // THE JOIN: the grant's channel mask was validated against the WORLD's channel table, and the guest's own
            // channel names were resolved against that same table at its handshake — and until now nothing compared the
            // two to each other. A consent row could therefore name a real channel its holder never emits, be accepted
            // in full, and drive nothing, leaving an operator to read that absence as a pool set too low or a body that
            // will not move. Reported, never refused: a later reload may legitimately add the channel, so the row is a
            // standing intent rather than a mistake.
            if (m_addons?.DescribeUndeclaredGrantedChannels(
                principal: grant.Principal,
                reach: grant.Reach,
                channels: m_population.Channels
            ) is { } undeclared) {
                Console.Error.WriteLine(value: $"[world.grant: {grant.Principal.Describe()} is granted channel(s) it never declares — inert until it does: {undeclared}]");
            }

            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: $"grant {label}{(grant.Exclusive
                ? " exclusive"
                : string.Empty)}",
                Rejected: false,
                Kind: WorldEditEchoKind.GrantTable,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return true;
        } else {
            Console.Error.WriteLine(value: $"[world.grant rejected: {label} — {reason}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: $"grant {label} rejected: {reason}",
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }
    }
    // THE BOOT CONSENT WITHHOLDING. A document row is applied under the CONSOLE principal, which
    // HoldsForAdministration exempts unconditionally — so without
    // this, the narrowing that stops a Seat from authoring a grant over anyone else's body would close only the live
    // verb and leave the document path wide open: a shipped row could hand an addon a pooled reach over a body its
    // human never consented to, and the human would inherit it the moment they sat down (occupancy at boot proves
    // nothing — no seat is active yet when this runs).
    //
    // The rule chosen is ADMIT THE ROW, WITHHOLD THE CONSENT: the ceiling — the number that is the consent, and the
    // only thing that lets an untrusted contribution move a human's body at all — never comes from a document. A
    // contributor's REACH mask still does, so a world can pre-wire an addon exactly as before; it simply contributes
    // nothing until a seat authors a ceiling live. Refusing the whole row instead would have cost that pre-wiring for
    // no additional safety, since a reach with no ceiling already folds nothing.
    //
    // The withholding is LOUD: a silently-narrowed row would read, in world.grants, as a document that never asked.
    private static WorldGrant WithoutAuthoredConsent(WorldGrant grant) {
        if (grant.Ceiling is null) {
            return grant;
        }

        Console.Error.WriteLine(value: $"[world.grant: {grant.Principal.Describe()} drive {grant.Subject.Describe()} — the document's ceiling is WITHHELD (a pooled ceiling is consent, and consent is authored live by the seated human on its own body, never shipped in a world document); the row applies with no pool]");

        // The mask travels with the ceiling on a seat's own gesture and means nothing without it, so both go.
        return (grant with { Reach = null, Consent = null, Ceiling = null });
    }

    /// <summary>Adds a grant to the table synchronously (the <c>world.grant</c> half; like a command, so the next tick's
    /// checks observe it). Checks <paramref name="actor"/> — the principal asking, distinct from
    /// <see cref="WorldGrant.Principal"/> (the principal receiving it) — via
    /// <see cref="WorldGrants.HoldsForAdministration"/>, which is enforced only for actors outside the trust boundary
    /// (an <c>Addon</c> or <c>Peer</c> may only grant authority it itself holds); a <c>Console</c> or <c>Seat</c> actor
    /// passes unconditionally, because gating a fully-trusted operator's own grant path is ceremony, not security — see
    /// the check's own doc for why. A denied actor prints the same loud, attributed line as a conflicting exclusive
    /// acquisition and changes nothing.</summary>
    /// <param name="grant">The grant to add.</param>
    /// <param name="actor">The principal asking for the grant to be added.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller (replay, the addon runtime) with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <remarks>A Drive grant whose subject is a remote-admitted human body
    /// (<see cref="WorldPopulation.IsAdmittedPeer"/>) refuses, by name, for any <see cref="WorldGrant.Principal"/>
    /// other than that body's own <see cref="PrincipalKind.Peer"/>: with no Peer-authored consent grammar,
    /// <c>Reach ∧ Consent</c> is <c>0</c> by construction for any other principal, so such a row would compose to
    /// nothing anyway; the refusal states this at the door instead of leaving an operator to infer it from a pool
    /// that silently never moves. <see cref="WorldPopulation.IsAdmittedPeer"/> is currently always
    /// <see langword="false"/>, so this door does not yet trigger.</remarks>
    public void Grant(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        _ = TryApplyGrant(
            actor: actor,
            connectionId: connectionId,
            correlationId: correlationId,
            grant: grant
        );
    /// <summary>Returns the concrete grant rows held by one principal. Transfer rollback captures these before a
    /// federated peer generation leaves so an aborted onward handoff can restore the exact source authority.</summary>
    public IReadOnlyList<WorldGrant> GrantRows(WorldPrincipal principal) => m_grants.Rows(principal: principal);
    /// <summary>Removes a grant from the table synchronously (the <c>world.revoke</c> half). Checks <paramref name="actor"/>
    /// against the same administration rule as <see cref="Grant"/> — enforced only for an <c>Addon</c>/<c>Peer</c> actor,
    /// which must itself hold <see cref="WorldGrant.Capability"/> over <see cref="WorldGrant.Subject"/> (ignoring the
    /// exclusivity override <see cref="WorldGrants.Allows"/> enforces at use, so an untrusted actor can always revoke an
    /// exclusive grant it itself authorized); a <c>Console</c> or <c>Seat</c> actor passes unconditionally — see
    /// <see cref="WorldGrants.HoldsForAdministration"/> for why gating the trusted side would only brick self-revocation
    /// without buying any security.</summary>
    /// <param name="grant">The grant (capability + subject) to revoke.</param>
    /// <param name="actor">The principal asking for the grant to be revoked.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    public void Revoke(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        if (!m_grants.HoldsForAdministration(
            principal: actor,
            capability: grant.Capability,
            subject: grant.Subject
        )) {
            DenyGrantTable(
                denial: $"{actor.Describe()} cannot revoke {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()} from {grant.Principal.Describe()} — it holds none there itself",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return;
        }

        var removed = m_grants.Revoke(
            principal: grant.Principal,
            capability: grant.Capability,
            subject: grant.Subject
        );

        Console.Error.WriteLine(value: (removed
            ? $"[world.revoke: {label}]"
            : $"[world.revoke: {grant.Principal.Describe()} held no {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()}]"));
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: (removed
            ? $"revoke {label}"
            : $"revoke {label} — nothing held"),
            Rejected: !removed,
            Kind: WorldEditEchoKind.GrantTable,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    /// <summary>
    /// Applies the admission predicate for a mutation — the one place the whole authority decision for a document write is
    /// made, and the only place any ingress is allowed to make it.
    /// </summary>
    /// <remarks>
    /// <para>Before the gates, one structural exemption: a <see cref="PrincipalKind.World"/> principal — the document
    /// acting on itself (a rule's effects, a kit's generate effect), never an actor — is admitted outright as
    /// <c>WorldMutationAdmissionRule.Structural</c>, before any authority is consulted. The gates below decide every
    /// other principal.</para>
    /// <para>Four gates, in order: (1) the coarse Mutate hold over the mutation's own document section, OR — when
    /// the mutation names one concrete creations/placements row — a Mutate hold over that row alone; (2) the
    /// deciding Mutate row's <see cref="MutationKindMask"/>; (3) for a state-row or state-cell write, the row-scoped
    /// Edit hold over the concrete <c>state:&lt;name&gt;</c> subject and, beneath it, that deciding row's own kind
    /// mask; (4) for an untrusted principal, the per-tick dispatch budget. "Deciding row" always means the rule the
    /// verdict itself reports — <c>ConcreteHold</c> beats <c>WildcardHold</c>, and a row-scoped hold decides in
    /// place of the section it stands in for — never a union of a concrete and a wildcard row's masks.</para>
    /// <para><b>Gate 1 is a disjunction, not a second narrowing.</b> Unlike gate 3, which requires the section hold
    /// AND the concrete Edit hold, a <c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c> row stands in for the
    /// section hold the holder does not have. A section grant keeps admitting every row (the boot seed's shape), a
    /// row grant admits its own row and nothing else — which is also what keeps the compose arms' replace-by-key
    /// behavior safe for a row-scoped grantee: it can never name another holder's row to collide with, so no
    /// ownership check belongs on the compose arm.</para>
    /// <para><b>An absent kind mask is full reach.</b> A mask is opt-in narrowing beneath an already deny-by-default
    /// capability, never a second authority check: Console legitimately holds maskless <c>Mutate/section:*</c> rows
    /// from the boot seed, so refuse-all-on-unmasked here would deny every trusted mutation in the engine. Untrusted
    /// strictness lives at the grant door instead (<c>WorldGrants.Conflicts</c> refuses a maskless untrusted
    /// Mutate/section row outright), which is what makes an unmasked untrusted row unreachable rather than
    /// permissive.</para>
    /// <para>Every mutating ingress passes this: <see cref="TryApplyMutation"/> for the ordered domain (loopback,
    /// console, and the <c>WorldTcpHost</c> peer door, which converge there), and the addon mutation seam's
    /// pre-flight (<c>WorldAddonRuntime.ResolveMutations</c>), which keeps its own earlier call site — it refuses
    /// before decode so a guest cannot probe the decoder for free — but as a call to this rule, never a second copy
    /// of it. Call-site duplication is fine; rule reimplementation is the defect class this predicate exists to
    /// close.</para>
    /// </remarks>
    /// <param name="principal">The acting principal, as its ingress stamped it.</param>
    /// <param name="section">The document section the mutation targets (<c>SectionOf</c>).</param>
    /// <param name="kindOrdinal">The mutation's declared kind ordinal (<see cref="WorldMutationKindCatalog"/>).</param>
    /// <param name="rowScopedEditSubject">The concrete <c>state:&lt;name&gt;</c> subject a state write names, or
    /// <see langword="null"/> when the mutation is not row-scoped — or when the caller cannot yet know it (the addon
    /// pre-flight runs before decode, so its state writes take gate (3) later, at apply).</param>
    /// <param name="rowScopedMutateSubject">The concrete <c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c>
    /// subject the mutation's target row addresses, or <see langword="null"/> when the mutation targets no such row
    /// or the caller cannot yet know it (the addon pre-flight runs before decode; it holds no row-scoped rows to
    /// begin with — the grant door refuses one to an addon as inert).</param>
    /// <param name="meter">Whether this call is the metering point for the dispatch. False only where the ingress
    /// already charged it (an addon act charged at its pre-flight, re-entering at apply).</param>
    /// <param name="admission">The decided outcome — which gate fired and the row-level evidence behind it.</param>
    /// <returns><see langword="true"/> when every gate cleared (and the dispatch was charged, when metered).</returns>
    public bool TryAdmitMutation(WorldPrincipal principal, WorldSection section, int kindOrdinal, GrantSubject? rowScopedEditSubject, GrantSubject? rowScopedMutateSubject, bool meter, out WorldMutationAdmission admission) {
        var sectionSubject = GrantSubject.Section(section: section);

        // THE ONE STRUCTURAL EXEMPTION, keyed on the principal KIND and decided HERE so nothing else has to know
        // about it — no bypass parameter threaded through the apply path, no seeded wildcard row standing in for
        // authority. The world's own authored program (a rule's effects, a kit's generate effect) is not an actor
        // submitting a write; it is the document acting on itself, exactly as a per-body ActionEffect has always
        // done without consulting this table at all. See WorldPrincipal.World for the full argument, including why
        // this is NOT the "handler constructs a principal to launder an identity" defect. Every gate BELOW authority
        // still runs unconditionally at the call site: compose, whole-document validate, envelope, solids.
        if (principal.Kind == PrincipalKind.World) {
            admission = new WorldMutationAdmission(
                Rule: WorldMutationAdmissionRule.Structural,
                Verdict: default,
                Subject: sectionSubject,
                DecidingSubject: sectionSubject,
                Mask: MutationKindMask.Empty,
                Budget: 0
            );

            return true;
        }
        var mutateVerdict = m_grants.Allows(
            capability: WorldCapability.Mutate,
            principal: principal,
            subject: sectionSubject
        );
        // GATE 1 IS A DISJUNCTION: the coarse section hold, OR — when the mutation names one concrete row of a
        // row-scoped section — a Mutate hold over that row alone. A section grant therefore keeps admitting every
        // row (the boot seed's own shape), while a row grant admits its own row and nothing else, which is what
        // makes a contribution slot expressible and what closes the replace-by-key hazard for its holder: it cannot
        // name another row to collide with. The row subject is derived from the mutation itself, so the two can
        // never address different sections.
        var decidingMutateSubject = ((mutateVerdict.Rule == GrantRule.WildcardHold)
            ? GrantSubject.All
            : sectionSubject
        );
        var checkedSubject = sectionSubject;

        if (!mutateVerdict.IsAllowed) {
            if (rowScopedMutateSubject is not { } rowSubject) {
                admission = new WorldMutationAdmission(
                    Rule: WorldMutationAdmissionRule.SectionDenied,
                    Verdict: mutateVerdict,
                    Subject: sectionSubject,
                    DecidingSubject: sectionSubject,
                    Mask: MutationKindMask.Empty,
                    Budget: 0
                );

                return false;
            }

            var rowVerdict = m_grants.Allows(
                capability: WorldCapability.Mutate,
                principal: principal,
                subject: rowSubject
            );

            if (!rowVerdict.IsAllowed) {
                admission = new WorldMutationAdmission(
                    Rule: WorldMutationAdmissionRule.RowScopedDenied,
                    Verdict: rowVerdict,
                    Subject: rowSubject,
                    DecidingSubject: sectionSubject,
                    Mask: MutationKindMask.Empty,
                    Budget: 0
                );

                return false;
            }

            // The row hold decided, so the row's own mask and budget govern from here. A WildcardHold cannot reach
            // this branch: Mutate/all would already have carried the section check above.
            mutateVerdict = rowVerdict;
            decidingMutateSubject = rowSubject;
            checkedSubject = rowSubject;
        }

        if (
            m_grants.TryGetKindMask(
            principal: principal,
            capability: WorldCapability.Mutate,
            subject: decidingMutateSubject,
            out var mutateMask
        ) &&
            !mutateMask.Contains(ordinal: kindOrdinal)
        ) {
            admission = new WorldMutationAdmission(
                Budget: 0,
                DecidingSubject: decidingMutateSubject,
                Mask: mutateMask,
                Rule: WorldMutationAdmissionRule.MaskedKind,
                Subject: checkedSubject,
                Verdict: mutateVerdict
            );

            return false;
        }

        // A state-row OR state-cell mutation is checked a SECOND time: Edit over the CONCRETE state:<name> subject the
        // mutation names — the SAME subject whether the write is whole-row (UpsertStateRow/RemoveStateRow) or per-cell
        // (UpsertStateCell/RemoveStateCell), beneath the coarse section-level Mutate hold above.
        // The domain-seeded Edit/all every seat and Console already holds reaches every row and
        // every cell until an operator deliberately narrows it (see WorldCapability.Edit's remarks).
        if (rowScopedEditSubject is { } editSubject) {
            var editVerdict = m_grants.Allows(
                capability: WorldCapability.Edit,
                principal: principal,
                subject: editSubject
            );

            if (!editVerdict.IsAllowed) {
                admission = new WorldMutationAdmission(
                    Rule: WorldMutationAdmissionRule.RowDenied,
                    Verdict: editVerdict,
                    Subject: editSubject,
                    DecidingSubject: editSubject,
                    Mask: MutationKindMask.Empty,
                    Budget: 0
                );

                return false;
            }

            var decidingEditSubject = ((editVerdict.Rule == GrantRule.WildcardHold)
                ? GrantSubject.All
                : editSubject
            );

            if (
                m_grants.TryGetKindMask(
                principal: principal,
                capability: WorldCapability.Edit,
                subject: decidingEditSubject,
                out var editMask
            ) &&
                !editMask.Contains(ordinal: kindOrdinal)
            ) {
                admission = new WorldMutationAdmission(
                    Budget: 0,
                    DecidingSubject: decidingEditSubject,
                    Mask: editMask,
                    Rule: WorldMutationAdmissionRule.RowMaskedKind,
                    Subject: editSubject,
                    Verdict: editVerdict
                );

                return false;
            }
        }

        // THE BUDGET, for every untrusted principal and no other — a mounted addon's guest compute and a remote peer's
        // submission are the same denial-of-service shape, so they owe the same per-tick ceiling from the same meter.
        // The budget is read off the DECIDING row (identical to the concrete subject for every reachable case: only a
        // trusted principal can hold Mutate/all, and trusted principals are unmetered). A held row with NO recorded
        // budget is unreachable by construction — WorldGrants.Conflicts refuses an untrusted Mutate row without one
        // before it can be added — so it REFUSES rather than dispatching unmetered: an unmetered dispatch silently
        // defeats the very budget this gate exists to enforce.
        if (
            meter &&
            !WorldGrants.IsTrusted(principal: principal)
        ) {
            if (!m_grants.TryGetBudget(
                principal: principal,
                capability: WorldCapability.Mutate,
                subject: decidingMutateSubject,
                out var budget
            )) {
                admission = new WorldMutationAdmission(
                    Rule: WorldMutationAdmissionRule.MissingBudget,
                    Verdict: mutateVerdict,
                    Subject: checkedSubject,
                    DecidingSubject: decidingMutateSubject,
                    Mask: MutationKindMask.Empty,
                    Budget: 0
                );

                return false;
            }

            if (!m_mutationBudget.TryCharge(
                budget: budget,
                principal: principal,
                section: section
            )) {
                admission = new WorldMutationAdmission(
                    Rule: WorldMutationAdmissionRule.BudgetExhausted,
                    Verdict: mutateVerdict,
                    Subject: checkedSubject,
                    DecidingSubject: decidingMutateSubject,
                    Mask: MutationKindMask.Empty,
                    Budget: budget
                );

                return false;
            }
        }

        admission = new WorldMutationAdmission(
            Rule: WorldMutationAdmissionRule.Admitted,
            Verdict: mutateVerdict,
            Subject: checkedSubject,
            DecidingSubject: decidingMutateSubject,
            Mask: MutationKindMask.Empty,
            Budget: 0
        );

        return true;
    }
}
