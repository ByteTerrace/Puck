using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    // PUMP POINT 1, per addon, run right after a successful Pump: the addon mutation seam's six-stage dispatch door
    // over every SubmitMutation act in THIS batch. Stages: (1) manifest, (2+3) THE SHARED ADMISSION PREDICATE —
    // WorldServer.TryAdmitMutation, the one owner of hold ∧ verb mask ∧ budget for every mutation ingress, called
    // here rather than reimplemented, and called BEFORE decode so a malformed payload still spends its dispatch —
    // (4) the reserved answer cell (bookkeeping only: the ABI handshake's outCap <= inCap-1 relation already proves
    // this can never overflow ReservedAnswers), (5) pointer safety (unsigned ptr/len, the payload-size ceilings, an
    // immediate host-side copy), (6) the per-kind decode (WorldAddonMutationDecoder). A cleared act ENQUEUES a
    // PendingOp.Mutate — it is NEVER applied here; application (compose -> revalidate -> swap) runs later THIS SAME
    // Step, at WorldServer.Step's DrainPendingOps, before intents. Every other outcome is DECIDED here but not yet
    // DELIVERED: every reserved slot's verdict is staged into the guest's next input batch by ResolveReads/
    // StageBatch — never here, and never by DrainPendingOps directly.
    private void ResolveMutations(MountedAddon addon, ulong tick) {
        addon.ReservedCount = 0;
        addon.MutateBytesThisTick = 0;

        var queries = addon.Pump.Queries;
        var grants = m_server.Grants;
        var handles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Mutate
        );
        var dispatchExhaustedThisTick = false;
        var byteExhaustedThisTick = false;

        try {
            for (var index = 0; (index < queries.Length); ++index) {
                ref readonly var query = ref queries[index];

                if (query.Verb != AddonAbi.RequestVerbs.SubmitMutation) {
                    continue;
                }

                // STAGE 4 — the reservation. Bookkeeping only: the slot's Verdict starts at AddonVerdict.None (the
                // "still pending" sentinel between decode and drain within this same Step) and every branch below
                // either overwrites it immediately or leaves it for CompleteMutation to overwrite at drain.
                var slot = addon.ReservedCount++;

                addon.ReservedAnswers[slot] = new AddonInCell(
                    Kind: AddonInCellKind.Answer,
                    Channel: ((byte)((addon.ResponseChannel < 0)
                    ? 0
                    : addon.ResponseChannel)),
                    Ordinal: query.Ordinal,
                    HandleIndex: 0,
                    HandleGeneration: 0,
                    Verdict: AddonVerdict.None,
                    Verb: 0,
                    A: 0L,
                    B: 0L
                );

                // The handle designates a SECTION subject; it never decides. Resolution failure is the revoked/
                // re-sorted case the generation check exists for — deliberately distinct from a denial.
                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Mutate
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Section)
                ) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.StaleHandle)
                    );
                    ReportStaleHandle(addon: addon);

                    continue;
                }

                // STAGE 1 — the manifest gate: requests ∧ grants. Checked before ANY further inspection, the same
                // enumeration-is-a-capability posture ResolveAsks/FoldActs already carry.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Mutate,
                    subject: subject
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.NotRequested)
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "mutated"
                    );

                    continue;
                }

                // STAGE 2+3 — THE SHARED ADMISSION PREDICATE. The bare Mutate hold, the DECIDING row's verb mask, and
                // the per-tick dispatch budget are ONE rule, owned by WorldServer.TryAdmitMutation and merely CALLED
                // here, before decode, so a malformed payload still spends its dispatch and a guest cannot probe the
                // decoder for free. Everything is re-checked live: a cached decision would go stale the moment
                // another principal reserves the section exclusively.
                //
                // rowScopedEditSubject is null: a state write's Edit/state:<name> subject is only knowable AFTER the
                // decode this pre-flight deliberately precedes, so that gate runs at apply (WorldServer's own
                // TryApplyMutation, later THIS same Step) over the identical predicate.
                //
                // meter: true — THIS is the metering point for a guest act; the apply path knows not to charge it
                // again (PendingOp.Mutate carries SourceAddonInstanceId for exactly that).
                var kindOrdinal = ((int)query.A);
                var section = ((WorldSection)subject.Value);

                if (!m_server.TryAdmitMutation(
                    principal: addon.Principal,
                    section: section,
                    kindOrdinal: kindOrdinal,
                    rowScopedEditSubject: null,
                    // Likewise null, for a stronger reason than the Edit subject's: the grant door refuses a
                    // row-scoped mutate row to an addon outright (this seam's handle designates a section and
                    // nothing else), so an addon never holds one to check.
                    rowScopedMutateSubject: null,
                    meter: true,
                    admission: out var admission
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: ToWireVerdict(admission: in admission)
                    );

                    if (admission.Rule == WorldMutationAdmissionRule.BudgetExhausted) {
                        dispatchExhaustedThisTick = true;

                        if (!addon.Mutate.ExhaustedReported) {
                            addon.Mutate.ExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} {admission.Describe()} — ordinal {query.Ordinal} refused QuotaExhausted]");
                        }
                    } else if (
                        (admission.Rule == WorldMutationAdmissionRule.MissingBudget) &&
                        !addon.Mutate.MissingBudgetReported
                    ) {
                        addon.Mutate.MissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} {admission.Describe()}; ordinal {query.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                // STAGE 5 — pointer safety: the per-payload ceiling, then the per-addon and global per-tick byte
                // ceilings (all THREE are size refusals, checked before a single guest-memory byte is read), then
                // an IMMEDIATE host-owned copy (AddonInstance.TryCopyMemory bounds-checks ptr/len against the
                // guest's ACTUAL memory length, unsigned throughout, overflow-checked end).
                //
                // query.C crosses the ABI as a signed i64 lane REINTERPRETED UNSIGNED (see
                // AddonSimulationPump.TryValidateQuery's remarks) — compared as ulong here, BEFORE any narrowing
                // cast, so a negative-reinterpreted-as-huge length reads as "too large" rather than wrapping into a
                // small or negative `int` that could slip under the ceiling check.
                var lengthUnsigned = unchecked((ulong)query.C);

                if (lengthUnsigned > ((ulong)AddonAbi.MaxMutationPayloadBytes)) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.PayloadTooLarge)
                    );

                    continue;
                }

                // Safe to narrow now: the check above already proved lengthUnsigned <= MaxMutationPayloadBytes
                // (8192), which fits in an int with room to spare.
                var length = ((int)lengthUnsigned);

                if ((addon.MutateBytesThisTick + length) > AddonAbi.MaxMutationBytesPerTickPerAddon) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.AddonByteBudgetExhausted)
                    );
                    byteExhaustedThisTick = true;

                    if (!addon.MutateByteBudgetExhaustedReported) {
                        addon.MutateByteBudgetExhaustedReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its per-tick mutation-payload byte budget ({AddonAbi.MaxMutationBytesPerTickPerAddon} bytes) — ordinal {query.Ordinal} refused QuotaExhausted]");
                    }

                    continue;
                }

                if ((m_mutationBytesThisTickAllAddons + length) > AddonAbi.MaxMutationBytesPerTickAllAddons) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.GlobalByteBudgetExhausted)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name}'s mutation payload ({length} bytes) would exceed the GLOBAL per-tick ceiling ({AddonAbi.MaxMutationBytesPerTickAllAddons} bytes, all addons summed) — ordinal {query.Ordinal} refused QuotaExhausted]");

                    continue;
                }

                addon.MutateBytesThisTick += length;
                m_mutationBytesThisTickAllAddons += length;

                if (!addon.Instance.TryCopyMemory(
                    pointer: query.B,
                    length: length,
                    destination: addon.MutationPayloadBuffer.AsSpan(
                        length: length,
                        start: 0
                    ),
                    error: out var copyError
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.PointerOutOfBounds)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} submit-mutation ordinal {query.Ordinal} refused MalformedPayload — {copyError}]");

                    continue;
                }

                var payload = addon.MutationPayloadBuffer.AsMemory(
                    length: length,
                    start: 0
                );

                // STAGE 6 — the per-kind hand-walked decode. On success the mutation is NOT applied here: it
                // enqueues as a PendingOp with this act's (addon instance id, ordinal) completion fields, drained
                // the SAME Step at WorldServer.Step's DrainPendingOps (before intents) through the identical
                // compose->revalidate->swap path a console-submitted mutation runs — CompleteMutation stages the
                // outcome (Applied or Rejected) into this slot once that drain decides it. The instance id, not this
                // guest's current position in m_mounted, is what travels with the pending op — a queued removal or
                // reorder that drains before this act's own completion must not deliver it to whatever guest now
                // occupies the old position.
                if (
                    !WorldAddonMutationDecoder.TryDecode(
                    kindOrdinal: kindOrdinal,
                    section: section,
                    payload: payload,
                    principal: addon.Principal,
                    mutation: out var mutation,
                    error: out var decodeError
                ) ||
                    (mutation is null)
                ) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.DecodeFailed)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} submit-mutation ordinal {query.Ordinal} refused MalformedPayload — {decodeError}]");

                    continue;
                }

                m_server.EnqueueMutation(
                    mutation: mutation,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    sourceAddonInstanceId: addon.InstanceId,
                    actOrdinal: query.Ordinal
                );
            }
        } finally {
            // Edge-triggered per exhaustion episode, the same shape every other dispatch-budget latch in this file
            // uses — reset the moment a tick exhausts neither ceiling, so a LATER episode can report again.
            if (!dispatchExhaustedThisTick) {
                addon.Mutate.ExhaustedReported = false;
            }

            if (!byteExhaustedThisTick) {
                addon.MutateByteBudgetExhaustedReported = false;
            }
        }
    }
    // Overwrites the reserved Answer cell for `ordinal` with its decided verdict — shared by a stage 1-5 refusal
    // decided at decode time and by CompleteMutation's stage-6-onward outcome decided later at drain. A miss (no
    // reservation for this ordinal) is silently ignored rather than thrown: a caller passing an unreserved ordinal
    // is a programming error to catch by review, not a runtime condition to crash a live session over.
    private static void SetReservedVerdict(MountedAddon addon, ushort ordinal, AddonVerdict verdict) {
        for (var index = 0; (index < addon.ReservedCount); ++index) {
            if (addon.ReservedAnswers[index].Ordinal == ordinal) {
                addon.ReservedAnswers[index] = addon.ReservedAnswers[index] with { Verdict = verdict };
                return;
            }
        }
    }
    // The one-directional map from the shared admission predicate's decided rule onto this door's own cataloged
    // refusal, and from there onto the wire verdict staged into the guest's reserved answer cell. Total over the
    // rules this pre-flight can actually reach — the three ROW-scoped rules cannot fire here (it passes neither
    // row-scoped subject; the Edit gate runs at apply, and an addon holds no row-scoped Mutate row at all), so a
    // rule arriving from them is a wiring change to make deliberately rather than a value to map by default.
    private static AddonVerdict ToWireVerdict(in WorldMutationAdmission admission) => admission.Rule switch {
        WorldMutationAdmissionRule.SectionDenied => WorldAddonWire.FromRule(rule: admission.Verdict.Rule),
        // A hold whose mask does not cover this kind answers as attenuation — "requested more than the mask admits"
        // attenuates to nothing, exactly like an unrequested Ask. A hold with NO mask is a DIFFERENT case now and
        // never reaches here: an absent mask means full reach at the predicate, and the grant door refuses a maskless
        // untrusted Mutate/section row outright, so this door can no longer be handed one.
        WorldMutationAdmissionRule.MaskedKind => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.MaskedKind),
        WorldMutationAdmissionRule.MissingBudget => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.MissingBudget),
        WorldMutationAdmissionRule.BudgetExhausted => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.DispatchBudgetExhausted),
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(admission),
        actualValue: admission.Rule,
        message: "unmapped mutation-admission rule at the addon pre-flight — extend the mapping deliberately, never default it"
    ),
    };

    /// <summary>Routes an addon-sourced mutation's decided outcome back to its originating guest's reserved answer
    /// cell — called by <see cref="WorldServer.Step"/>'s <c>DrainPendingOps</c>, in the
    /// same Step the act was decoded, immediately after the mutation's compose→revalidate→swap ran. Never applies
    /// anything itself; it only records which verdict <see cref="ResolveReads"/> will stage into the guest's next
    /// batch. Addressed by the mounted instance's own stable token, captured at decode time, rather than its
    /// position in <c>m_mounted</c> — a queued removal or reorder that drains before this completion would move a
    /// positional index onto a DIFFERENT guest, but the token still names only the instance the act was actually
    /// decoded from. A no-op when no currently-mounted guest carries <paramref name="addonInstanceId"/> — the
    /// originating instance was removed, or reloaded into a fresh instance under a new token, since decode; a
    /// reload's fresh guest never receives an act decoded from the instance it replaced.</summary>
    /// <param name="addonInstanceId">The mounted addon instance token the act was decoded from.</param>
    /// <param name="actOrdinal">The addon's own output-batch ordinal the act answers.</param>
    /// <param name="applied">Whether the document-apply pipeline accepted the decoded mutation.</param>
    public void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied) {
        var addon = FindMountedByInstanceId(instanceId: addonInstanceId);

        if (addon is null) {
            return;
        }

        SetReservedVerdict(
            addon: addon,
            ordinal: actOrdinal,
            verdict: (applied
            ? AddonVerdict.Applied
            : AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.ApplyRejected))
        );
    }

    private MountedAddon? FindMountedByInstanceId(long instanceId) {
        for (var index = 0; (index < m_mounted.Count); ++index) {
            if (m_mounted[index].InstanceId == instanceId) {
                return m_mounted[index];
            }
        }

        return null;
    }
}
