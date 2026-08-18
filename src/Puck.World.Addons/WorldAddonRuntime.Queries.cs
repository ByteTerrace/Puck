using System.Text;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private static void QueuePart(MountedAddon addon, ushort ordinal, AddonVerdict verdict, byte part, long a, long b) {
        addon.Answers[addon.AnswerCount++] = new AddonInCell(
            Kind: AddonInCellKind.Answer,
            Channel: ((byte)addon.ResponseChannel),
            Ordinal: ordinal,
            HandleIndex: 0,
            HandleGeneration: 0,
            Verdict: verdict,
            Verb: part,
            A: a,
            B: b
        );
    }
    // Asks: mint a handle over a subject the guest NAMES, resolved requested ∧ granted. The mask is single-bit and
    // defined (the pump proved both), the subject is range- then existence-checked here, and the mint is by requested
    // subject — the host projects, the guest never names a table position.
    private void ResolveAsks(MountedAddon addon) {
        var asks = addon.Pump.Asks;
        var grants = m_server.Grants;

        for (var index = 0; (index < asks.Length); ++index) {
            ref readonly var ask = ref asks[index];

            if (!WorldAddonWire.TryCapability(
                mask: ask.CapabilityMask,
                capability: out var capability
            )) {
                // Unreachable: the pump admits only the guest-maskable bits. If it ever fires, the wire mapping and
                // the pump's own mask set have drifted apart and the guest must not be left guessing.
                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: $"ask ordinal {ask.Ordinal} carries capability mask 0x{ask.CapabilityMask:x}, which maps to no engine capability"
                    );
                }

                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            // The subject shape is per-KIND (Body pairs with Drive/Observe, Section pairs with Mutate — the pump
            // already enforced that pairing at TryValidateAsk), so the RANGE check and the GrantSubject construction
            // both branch on it here. A subject kind neither the pump nor this switch recognizes falls to the safe
            // default: out of range.
            //
            // Section is NAME-KEYED, never ordinal-keyed: a guest sends its section's declared NAME (UTF-8 bytes in
            // its own linear memory, ptr+len in the ask's A/C lanes — the same convention SubmitMutation uses for a
            // payload), and the host resolves it against the live WorldSection vocabulary here. There is no ordinal
            // for a guest to bake stale, and an unresolvable name refuses LOUDLY, quoting the name, rather than
            // silently minting authority over an unintended member.
            bool inRange;
            GrantSubject subject;

            if (ask.SubjectKind == AddonSubjectKind.Body) {
                inRange = ((ask.SubjectIndex >= 0L) && (ask.SubjectIndex < m_server.Population.Capacity));
                subject = (inRange
                    ? GrantSubject.Body(index: ((int)ask.SubjectIndex))
                    : GrantSubject.All
                );
            } else if (ask.SubjectKind == AddonSubjectKind.Section) {
                if (!TryResolveSectionAskName(
                    addon: addon,
                    ask: in ask,
                    error: out var copyError,
                    name: out var sectionName,
                    refusal: out var copyRefusal
                )) {
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} ask ordinal {ask.Ordinal} refused {copyRefusal} — {copyError}]");
                    QueueAnswer(
                        addon: addon,
                        ordinal: ask.Ordinal,
                        verdict: copyRefusal!.Value
                    );

                    continue;
                }

                // No manifest-gate deferral here, unlike Body's liveness check below: the WorldSection vocabulary is
                // a fixed, PUBLIC set (every member name ships in this repository's own docs and console grammar),
                // so answering "no such section" for an unresolvable name leaks nothing a body-liveness answer
                // would — there is no enumeration oracle to protect against for a static enum.
                if (!GrantSubject.TryParseSectionName(
                    name: sectionName,
                    section: out var resolvedSection
                )) {
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} ask ordinal {ask.Ordinal} refused NoSuchSubject — unknown section name '{sectionName}']");
                    QueueAnswer(
                        addon: addon,
                        ordinal: ask.Ordinal,
                        verdict: AddonVerdict.NoSuchSubject
                    );

                    continue;
                }

                inRange = true;
                subject = GrantSubject.Section(section: resolvedSection);
            } else {
                inRange = false;
                subject = GrantSubject.All;
            }

            // The MANIFEST gate runs before any further inspection, including the liveness check below: answering
            // NoSuchSubject before this gate would make the verdict a body-enumeration oracle (live body vs empty
            // slot leaks off the difference) for a zero-grant guest. An index the manifest could not have named
            // (out of range, or an unrecognized kind) is attenuated for the same reason.
            if (
                !inRange ||
                !IsRequested(
                addon: addon,
                capability: capability,
                subject: subject
            )
            ) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.AttenuatedToEmpty
                );

                continue;
            }

            // Liveness only ever answers for a subject the guest's OWN manifest names — no oracle — and only ever
            // applies to a BODY subject: a document section is a fixed enum member, not a live population entry, so
            // it has no analogous "does not exist right now" state to check.
            if (
                (ask.SubjectKind == AddonSubjectKind.Body) &&
                (m_server.Body(index: ((int)ask.SubjectIndex)) is null)
            ) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoSuchSubject
                );

                continue;
            }

            var verdict = grants.Allows(
                principal: addon.Principal,
                capability: capability,
                subject: subject
            );

            if (!verdict.IsAllowed) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: WorldAddonWire.FromRule(rule: verdict.Rule)
                );

                continue;
            }

            // Mint by requested subject through the table's own cached projection — no per-ask array allocation, no
            // linear re-scan of a projection the table already holds.
            if (!grants.HandleTable(
                principal: addon.Principal,
                capability: capability
            ).TryMintFor(
                handle: out var handle,
                subject: subject
            )) {
                // Allowed but unprojected — a wildcard hold, which the grant door refuses outright for an addon, so
                // this is unreachable today. Answering NoHold rather than minting a handle over a subject no slot names
                // is the safe half of the discrepancy; the line is the other half.
                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: $"holds {capability.ToString().ToLowerInvariant()} over {subject.Describe()} by {verdict.Describe()} but no handle slot projects it — no handle was minted"
                    );
                }

                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            if (!TryPack(
                addon: addon,
                generation: out var wireGeneration,
                handle: handle,
                index: out var wireIndex
            )) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            QueueAnswer(
                addon: addon,
                ordinal: ask.Ordinal,
                verdict: WorldAddonWire.FromRule(rule: verdict.Rule),
                handleIndex: wireIndex,
                handleGeneration: wireGeneration
            );
        }
    }
    // Queries: a pose read through an Observe handle. Four answer cells share the request's ordinal on the guest's
    // Response channel, each repeating the SAME allowing verdict and carrying the part index in the Verb byte, with both
    // handle lanes zero — a pose grants no handle. Host-written explicit framing, never an implied pairing the guest has
    // to reconstruct.
    private void ResolveQueries(MountedAddon addon) {
        var queries = addon.Pump.Queries;
        var grants = m_server.Grants;
        var handles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Observe
        );
        var driveHandles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Drive
        );
        // Set the moment any subject exhausts its observe budget THIS tick — read by the edge-trigger reset in the
        // finally below, the same shape as MergeAnswers' QuotaDropReported. The loop is wrapped in try/finally so an
        // unexpected throw partway through still runs the reset decision, the same hardening FoldActs' Drive twin
        // carries — an episode's caller must never be able to leave the latch stuck on the strength of an exception
        // it didn't plan for.
        var exhaustedThisTick = false;

        try {
            for (var index = 0; (index < queries.Length); ++index) {
                ref readonly var query = ref queries[index];

                // SubmitMutation acts already ran the WHOLE six-stage dispatch door at decode time
                // (TickAddons -> ResolveMutations, pump point 1) — their reserved answer cell is staged directly by
                // StageBatch, never through this method's Observe-only path. Skipping here (rather than falling
                // into the "verb not served" discrepancy branch below) keeps that branch meaning what it says: a
                // verb this host genuinely does not recognize, not one a DIFFERENT stage already answered.
                if (query.Verb == AddonAbi.RequestVerbs.SubmitMutation) {
                    continue;
                }

                if (query.Verb == AddonAbi.RequestVerbs.Designate) {
                    var sourceHandle = new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Drive
                    );

                    if (
                        !driveHandles.TryResolve(
                        handle: sourceHandle,
                        subject: out var sourceSubject
                    ) ||
                        (sourceSubject.Kind != GrantSubjectKind.Body)
                    ) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.StaleHandle
                        );
                        continue;
                    }
                    if (!IsRequested(
                        addon: addon,
                        capability: WorldCapability.Drive,
                        subject: sourceSubject
                    )) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.AttenuatedToEmpty
                        );
                        ReportUnrequestedAct(
                            addon: addon,
                            subject: sourceSubject,
                            via: "designated"
                        );
                        continue;
                    }

                    var targetSubject = GrantSubject.Body(index: ((int)query.A));

                    if (!IsRequested(
                        addon: addon,
                        capability: WorldCapability.Observe,
                        subject: targetSubject
                    )) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.AttenuatedToEmpty
                        );
                        ReportUnrequestedAct(
                            addon: addon,
                            subject: targetSubject,
                            via: "designated"
                        );
                        continue;
                    }

                    var registerIndex = ((int)query.B);

                    if (((uint)registerIndex) >= ((uint)m_server.Population.TargetRegisters.Count)) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.Rejected
                        );
                        continue;
                    }

                    var applied = m_server.ApplyDesignation(
                        designation: new WorldDesignation(
                            EntityIndex: sourceSubject.Value,
                            Register: m_server.Population.TargetRegisters.Name(index: registerIndex),
                            Subject: targetSubject
                        ),
                        principal: addon.Principal
                    );

                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: (applied
                        ? AddonVerdict.Applied
                        : AddonVerdict.Rejected)
                    );
                    continue;
                }

                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Observe
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Body)
                ) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.StaleHandle
                    );

                    continue;
                }

                // The manifest gate, at the read exactly as at the act: the projection table resolves any fabricated
                // (index, generation) pair that lands on a live slot, so a granted-but-unrequested body would otherwise be
                // readable through a guessed handle even though disclosure and Ask both withhold it.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Observe,
                    subject: subject
                )) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "read"
                    );

                    continue;
                }

                // The handle designated the subject; the grant table decides whether the read may happen, re-checked here
                // because a cached decision would go stale the moment another principal reserves the subject exclusively.
                var verdict = grants.Allows(
                    principal: addon.Principal,
                    capability: WorldCapability.Observe,
                    subject: subject
                );

                if (!verdict.IsAllowed) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: WorldAddonWire.FromRule(rule: verdict.Rule)
                    );

                    continue;
                }

                // BUDGET CHECK — charge order is resolve -> IsRequested -> Allows -> budget -> dispatch: after the
                // authority verdicts (so a denial stays precise and costs no budget) and before the dispatch it
                // meters (the read below, and later a spatial verb's raymarch). Read fresh per query, like Allows and
                // for the identical staleness reason: a re-grant with a different budget takes effect on THIS query.
                //
                // A row with NO recorded budget is UNREACHABLE BY CONSTRUCTION: every principal reaching
                // ResolveQueries is a mounted addon's own untrusted Principal, and TryGrant's own Conflicts gate
                // already refuses an untrusted Observe grant that carries no budget before it can be added — so an
                // Observe hold for this principal cannot exist without a matching budget entry. If this branch ever
                // fires, the grant table itself has gone inconsistent, so it REFUSES the query rather than
                // dispatching it unmetered. It reuses the Allows-denied branch's NoHold verdict and reports through
                // its OWN latch so it can never be starved by DiscrepancyReported firing first at an unrelated site.
                if (grants.TryGetBudget(
                    principal: addon.Principal,
                    capability: WorldCapability.Observe,
                    subject: subject,
                    out var budget
                )) {
                    if (addon.DispatchCounts[subject.Value] >= budget) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.QuotaExhausted
                        );
                        exhaustedThisTick = true;

                        if (!addon.DispatchBudgetExhaustedReported) {
                            addon.DispatchBudgetExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its observe/{subject.Describe()} dispatch budget ({budget}/tick) — ordinal {query.Ordinal} refused QuotaExhausted]");
                        }

                        continue;
                    }

                    addon.DispatchCounts[subject.Value]++;
                } else {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.NoHold
                    );

                    if (!addon.MissingBudgetReported) {
                        addon.MissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} holds observe over {subject.Describe()} with no recorded dispatch budget — an authority-table inconsistency (unreachable by construction); ordinal {query.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                if (m_server.Body(index: subject.Value) is not { } body) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.NoSuchSubject
                    );

                    continue;
                }

                if (query.Verb != AddonAbi.RequestVerbs.BodyPose) {
                    // Unreachable: the pump range-checks the verb against the guest's declared count, which the closed
                    // request vocabulary bounds. A verb this host cannot serve is named loudly rather than answered with a
                    // verdict that would misdescribe it as an authority outcome.
                    if (!addon.DiscrepancyReported) {
                        ReportDiscrepancy(
                            addon: addon,
                            detail: $"request verb {query.Verb} at ordinal {query.Ordinal} is not served by this host — no answer was produced"
                        );
                    }

                    continue;
                }

                var allowed = WorldAddonWire.FromRule(rule: verdict.Rule);
                var position = body.FixedPosition;
                var orientation = body.FixedOrientation;

                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 0,
                    a: position.X.Value,
                    b: position.Y.Value
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 1,
                    a: position.Z.Value,
                    b: 0L
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 2,
                    a: orientation.X.Value,
                    b: orientation.Y.Value
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 3,
                    a: orientation.Z.Value,
                    b: orientation.W.Value
                );
            }
        } finally {
            // DispatchBudgetExhaustedReported is EDGE-TRIGGERED per exhaustion episode (reset here the moment a tick
            // exhausts no observe budget), never a once-per-process-lifetime latch — the same shape as MergeAnswers'
            // QuotaDropReported, for the identical reason: a second, later saturation episode must be able to say so
            // again rather than staying silent forever after the first.
            if (!exhaustedThisTick) {
                addon.DispatchBudgetExhaustedReported = false;
            }
        }
    }
    // Copies a Section ask's name bytes out of the guest's OWN linear memory — the pointer-safety stage of the
    // name-keyed ask boundary, mirroring ResolveMutations' identical copy for a SubmitMutation payload: a length
    // ceiling check (AddonAbi.MaxSectionNameBytes) before a single byte is read, then an immediate host-owned copy
    // via AddonInstance.TryCopyMemory (bounds-checked against the guest's actual memory length). Both failure modes
    // are refused on THIS ask alone, never a whole-instance fault — a guest naming a bad pointer or an oversized
    // length gets a same-shape refusal on the ask, exactly like a malformed mutation payload does.
    private static bool TryResolveSectionAskName(MountedAddon addon, in AddonAskSubmission ask, out string name, out AddonVerdict? refusal, out string error) {
        var lengthUnsigned = unchecked((ulong)ask.NameLength);

        if (lengthUnsigned > ((ulong)AddonAbi.MaxSectionNameBytes)) {
            name = "";
            refusal = AddonVerdict.PayloadTooLarge;
            error = $"section-name length {ask.NameLength} exceeds {AddonAbi.MaxSectionNameBytes}";

            return false;
        }

        var length = ((int)lengthUnsigned);

        Span<byte> buffer = stackalloc byte[length];

        if (!addon.Instance.TryCopyMemory(
            pointer: ask.SubjectIndex,
            length: length,
            destination: buffer,
            error: out var copyError
        )) {
            name = "";
            refusal = AddonVerdict.MalformedPayload;
            error = copyError;

            return false;
        }

        name = Encoding.UTF8.GetString(bytes: buffer);
        refusal = null;
        error = "";

        return true;
    }
}
