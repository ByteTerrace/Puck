using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    // This tick's contribution restricted to the COMPOSITION ordinals — the held-device image's own convention (see
    // WorldBody.SetHeldChannels and SeatController.HeldChannels): a movement role rides the submitted intent and is
    // ignored on this path, so publishing one here would be a value nothing reads. Stack-only: ChannelValues is an
    // InlineArray, so this allocates nothing.
    private static PlayerIntent CompositionChannels(ChannelValues values, WorldChannelTable channels) {
        var composition = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ++ordinal) {
            if (!channels.IsRole(ordinal: ordinal)) {
                composition[ordinal] = values[ordinal];
            }
        }

        return new PlayerIntent(Channels: composition);
    }
    // Locate (or open) the accumulator for a body, keeping the array sorted ascending by body index. Bounded by the
    // guest's output capacity, so the insertion shift is over at most that many entries and never allocates.
    private static int Contribution(MountedAddon addon, int bodyIndex) {
        var contributions = addon.Contributions;
        var count = addon.ContributionCount;
        var slot = 0;

        while (
            (slot < count) &&
            (contributions[slot].BodyIndex < bodyIndex)
        ) {
            ++slot;
        }

        if (
            (slot < count) &&
            (contributions[slot].BodyIndex == bodyIndex)
        ) {
            return slot;
        }

        for (var index = count; (index > slot); --index) {
            contributions[index] = contributions[(index - 1)];
        }

        contributions[slot] = new BodyContribution(bodyIndex: bodyIndex);
        addon.ContributionCount = (count + 1);

        return slot;
    }
    // Mirrors StageContribution's own trusted-addon acceptance gate: a document-mounted addon is trusted-by-authorship
    // (added outside the pool), but still gated by its OWN declared Reach (WorldGrants.TryGetChannelReach) — there is
    // no occupying-seat ceiling to consult, unlike a genuinely untrusted (pooled) contributor. Recomputed here rather
    // than read back from the fold, because ApplyIntentSubmission's verdict answers Drive authority only.
    private bool ContributionAccepted(int bodyIndex, WorldPrincipal principal, in ChannelValues values) {
        if (!m_server.Grants.TryGetChannelReach(
            principal: principal,
            subject: GrantSubject.Body(index: bodyIndex),
            mask: out var reach
        )) {
            return false;
        }

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (
                (values[ordinal].Value != 0L) &&
                reach.Contains(ordinal: ordinal)
            ) {
                return true;
            }
        }

        return false;
    }
    // The read-only twin of Contribution: locate an existing accumulator, never open one.
    private static int FindContribution(MountedAddon addon, int bodyIndex) {
        for (var slot = 0; (slot < addon.ContributionCount); ++slot) {
            if (addon.Contributions[slot].BodyIndex == bodyIndex) {
                return slot;
            }
        }

        return -1;
    }
    // Fold one validated act into its body's accumulating channel vector, at the ordinal the guest's declared name
    // resolved to at handshake — the WORLD table's ordinal, and therefore a PlayerIntent ordinal directly. Every
    // channel is DECLARATIVE, this tick only: the host holds no channel state between ticks, so a channel a guest
    // stops emitting reads zero the very next tick, like a seat's own analog clear. The pump already refused a
    // duplicate ordinal within one batch as a protocol fault, so no act can overwrite another's channel here.
    private static void Fold(MountedAddon addon, int slot, in AddonActSubmission act) {
        ref var contribution = ref addon.Contributions[slot];

        // No hidden negation or axis remapping: the world channel's documented convention IS the wire convention.
        // The Rust guest is the one that must emit the correctly-signed value (see wasm/puck-addon-default) — the
        // old raw-stick negation lived here only because that guest used to speak raw stick space, not the intent's.
        contribution.Values[act.ChannelOrdinal] = FixedQ4816.FromRawBits(value: act.Value);
    }
    // PUMP POINT 2, per addon. Two passes over the staged acts with the submissions between them: the first resolves
    // handles and accumulates per-body axes, the second answers every ordinal whose body refused. Two passes rather than
    // one because a refusal is a property of the BODY (missing, or denied), and the acts that contributed to it are only
    // known once the whole batch has been folded.
    private void FoldActs(MountedAddon addon, ulong tick) {
        var acts = addon.Pump.Acts;
        var principal = addon.Principal;
        var handles = m_server.Grants.HandleTable(
            capability: WorldCapability.Drive,
            principal: principal
        );

        addon.ContributionCount = 0;
        // Per-tick Drive dispatch meter reset — the same shape as StageBatch's DispatchCounts clear for Observe,
        // just reset here because FoldActs (pump point 2) runs before StageBatch (pump point 3) within one tick.
        Array.Clear(array: addon.DriveDispatchCounts);
        // Set the moment any subject exhausts its drive budget THIS tick — read by the edge-trigger reset in the
        // finally below. The method body is wrapped in try/finally so an unexpected throw partway through still
        // runs the reset decision, rather than leaving the latch stuck armed.
        var driveExhaustedThisTick = false;

        try {
            for (var index = 0; (index < acts.Length); ++index) {
                ref readonly var act = ref acts[index];

                addon.ActBody[index] = NoBody;

                // Report-and-inert: a declared channel the host table doesn't recognize answers the SAME attenuation
                // verdict an unrequested subject does, reusing that posture rather than inventing a second one — the
                // act was well-formed, it simply names authority (a channel) that does not exist to grant. This is a
                // property of the ACT, known without any handle lookup, so it is checked before resolving one.
                if (!act.Resolved) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );

                    continue;
                }

                // A handle DESIGNATES; it never decides. Resolution failure is the revoked/re-sorted case the generation
                // check exists for, and it is deliberately distinct from a denial: withdrawn and never-granted are
                // different states. The kind is checked after resolving because a table guarantees only that a slot names
                // one instance of the capability's domain, never which kind that instance is.
                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: act.HandleIndex,
                        Generation: act.HandleGeneration,
                        TablePrincipal: principal,
                        TableCapability: WorldCapability.Drive
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Body)
                ) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.StaleHandle
                    );
                    ReportStaleHandle(addon: addon);

                    continue;
                }

                // The manifest gate, at application: minting filters requested ∧ granted, but the projection table
                // resolves ANY (index, generation) pair that matches a live slot — generations start at 0 and climb
                // slowly, so a guest can fabricate a plausible handle it was never handed. A resolve that lands on a
                // subject the manifest never requested is therefore refused as attenuation, exactly as an Ask for it
                // would be — never applied on the strength of the table alone.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Drive,
                    subject: subject
                )) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "drove"
                    );

                    continue;
                }

                // BUDGET CHECK — the Drive twin of ResolveQueries' Observe budget, same charge order (resolve ->
                // requested -> budget -> dispatch/fold): Drive's own "allowed" check happens once per BODY at Submit
                // (below), not once per act, so the budget meters the compute this act's resolve+fold already spent.
                // A row with no recorded budget is unreachable by construction: every principal reaching here is a
                // mounted addon's own untrusted Principal, and TryGrant's Conflicts gate refuses an untrusted Drive
                // hold with no budget before it can be added — so this refuses rather than dispatching unmetered.
                if (m_server.Grants.TryGetBudget(
                    principal: principal,
                    capability: WorldCapability.Drive,
                    subject: subject,
                    out var driveBudget
                )) {
                    if (addon.DriveDispatchCounts[subject.Value] >= driveBudget) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: act.Ordinal,
                            verdict: AddonVerdict.QuotaExhausted
                        );
                        driveExhaustedThisTick = true;

                        if (!addon.DriveDispatchBudgetExhaustedReported) {
                            addon.DriveDispatchBudgetExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its drive/{subject.Describe()} dispatch budget ({driveBudget}/tick) — ordinal {act.Ordinal} refused QuotaExhausted]");
                        }

                        continue;
                    }

                    addon.DriveDispatchCounts[subject.Value]++;
                } else {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.NoHold
                    );

                    if (!addon.DriveMissingBudgetReported) {
                        addon.DriveMissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} holds drive over {subject.Describe()} with no recorded dispatch budget — an authority-table inconsistency (unreachable by construction); ordinal {act.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                var bodyIndex = subject.Value;
                var slot = Contribution(
                    addon: addon,
                    bodyIndex: bodyIndex
                );

                addon.ActBody[index] = bodyIndex;
                Fold(
                    act: in act,
                    addon: addon,
                    slot: slot
                );
            }

            // Ascending body index, always — the contribution array is kept sorted on insert, so the order two acts land in
            // never depends on which handle the guest happened to name first.
            for (var slot = 0; (slot < addon.ContributionCount); ++slot) {
                Submit(
                    addon: addon,
                    slot: slot,
                    tick: tick
                );
            }

            for (var index = 0; (index < acts.Length); ++index) {
                var bodyIndex = addon.ActBody[index];

                if (bodyIndex == NoBody) {
                    continue;
                }

                // Every ActBody value here was set by a Contribution call in the first pass, so this always resolves.
                var outcome = addon.Contributions[FindContribution(
                    addon: addon,
                    bodyIndex: bodyIndex
                )].Outcome;

                // An allowed contribution answers nothing: silence is the positive signal.
                if (outcome != AddonVerdict.None) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: acts[index].Ordinal,
                        verdict: outcome
                    );
                }
            }
        } finally {
            // DriveDispatchBudgetExhaustedReported is EDGE-TRIGGERED per exhaustion episode (reset here the moment a
            // tick exhausts no drive budget), never a once-per-process-lifetime latch — the same shape as
            // MergeAnswers' QuotaDropReported, for the identical reason: a second, later saturation episode must be
            // able to say so again rather than staying silent forever after the first. The finally makes this run
            // even if the try above threw, rather than leaving the latch wherever the last successful tick left it.
            if (!driveExhaustedThisTick) {
                addon.DriveDispatchBudgetExhaustedReported = false;
            }
        }
    }
    // Sort the tick's answers into (ordinal, part) order and place them behind the disclosures, whole groups at a time:
    // a multi-part answer is atomic, because half a pose is a value the guest cannot tell apart from a whole one. A
    // group that no longer fits collapses to a single QuotaExhausted cell so the guest reads a refusal rather than
    // inferring one from an answer that never came. Once even that one cell does not fit, the remaining groups drop
    // with no verdict cell at all — the ring is physically full, and the ABI's ordinal contract rules out inventing a
    // many-to-one aggregate cell to say so on the wire without a real ABI change. addon.TotalAnswersDropped turns the
    // magnitude into a DURABLE, host-observable quantity (world.addons) rather than a fact that only ever existed on
    // a stderr line the instant it scrolled past. QuotaDropReported is EDGE-TRIGGERED per saturation episode (reset
    // in the finally below the moment a tick does not drop anything), never a once-per-process-lifetime latch, and
    // is wrapped in try/finally so the caller can never leave it stuck on the strength of a throw it didn't plan for.
    private static void MergeAnswers(MountedAddon addon, int budget) {
        SortAnswers(
            answers: addon.Answers,
            count: addon.AnswerCount
        );

        var index = 0;
        var refusing = false;
        var droppedGroupCount = 0;

        try {
            while (index < addon.AnswerCount) {
                var ordinal = addon.Answers[index].Ordinal;
                var end = index;

                while (
                    (end < addon.AnswerCount) &&
                    (addon.Answers[end].Ordinal == ordinal)
                ) {
                    ++end;
                }

                var size = (end - index);
                var remaining = (budget - addon.PendingCount);

                // Once one group fails to fit whole, EVERY later group is refused too — "that request and all later
                // ones" — never let a smaller later group slip through whole, or which answers a guest receives would
                // depend on the SIZES of its earlier requests rather than their order.
                if (
                    !refusing &&
                    (size > remaining)
                ) {
                    refusing = true;
                }

                if (!refusing) {
                    for (var part = index; (part < end); ++part) {
                        addon.Pending[addon.PendingCount++] = addon.Answers[part];
                    }
                } else if (remaining >= 1) {
                    addon.Pending[addon.PendingCount++] = new AddonInCell(
                        Kind: AddonInCellKind.Answer,
                        Channel: ((byte)addon.ResponseChannel),
                        Ordinal: ordinal,
                        HandleIndex: 0,
                        HandleGeneration: 0,
                        Verdict: AddonVerdict.QuotaExhausted,
                        Verb: 0,
                        A: 0L,
                        B: 0L
                    );
                } else {
                    ++droppedGroupCount;
                }

                index = end;
            }

            if (droppedGroupCount > 0) {
                addon.TotalAnswersDropped = ((addon.TotalAnswersDropped > (ulong.MaxValue - ((ulong)droppedGroupCount)))
                    ? ulong.MaxValue
                    : (addon.TotalAnswersDropped + ((ulong)droppedGroupCount))
                );

                if (!addon.QuotaDropReported) {
                    addon.QuotaDropReported = true;
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} filled its {budget}-cell answer budget — {droppedGroupCount} request group(s) this tick got no verdict cell at all (lifetime total {addon.TotalAnswersDropped}, see world.addons); shrink the batch or grow puck_in_cap]");
                }
            }
        } finally {
            if (droppedGroupCount == 0) {
                addon.QuotaDropReported = false;
            }
        }
    }
    private static void ReportFault(MountedAddon addon) {
        // A Disabled instance carries no fault detail; only a genuine fault has something to say.
        if (
            addon.FaultReported ||
            (addon.Instance.Fault.Kind == AddonFaultKind.None)
        ) {
            return;
        }

        addon.FaultReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Fault.Detail}]");
    }
    // This tick's contribution restricted to the MOVEMENT-ROLE ordinals — the submitted intent's own convention (see
    // SeatController.HeldIntent). Stack-only: ChannelValues is an InlineArray, so this allocates nothing.
    private static PlayerIntent RoleChannels(ChannelValues values, WorldChannelTable channels) {
        var roles = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ++ordinal) {
            if (channels.IsRole(ordinal: ordinal)) {
                roles[ordinal] = values[ordinal];
            }
        }

        return new PlayerIntent(Channels: roles);
    }
    // Insertion sort by (ordinal, part). The answers arrive as at most three already-ascending runs (act refusals, ask
    // answers, query parts), so this is near-linear in practice and never allocates; a stable order matters because a
    // pose's four parts must reach the guest in part order.
    private static void SortAnswers(AddonInCell[] answers, int count) {
        for (var index = 1; (index < count); ++index) {
            var candidate = answers[index];
            var slot = (index - 1);

            while (
                (slot >= 0) &&
                ((answers[slot].Ordinal > candidate.Ordinal) || ((answers[slot].Ordinal == candidate.Ordinal) && (answers[slot].Verb > candidate.Verb)))
            ) {
                answers[(slot + 1)] = answers[slot];
                --slot;
            }

            answers[(slot + 1)] = candidate;
        }
    }
    // PUMP POINT 3, per addon: disclosures, then events, then asks, then queries, then the budgeted merge into the next tick's batch.
    private void StageBatch(MountedAddon addon, ulong tick) {
        addon.PendingCount = 0;
        // Per-tick dispatch meter reset, beside the other per-tick scratch above — a fresh tick owes each budgeted
        // row its full allowance again.
        Array.Clear(array: addon.DispatchCounts);
        addon.EventCounts.Clear();

        // A guest that declared no Response channel can be handed nothing: every answer and every grant disclosure is
        // undeliverable by construction, which also means it can never learn a handle and therefore can never reach a
        // body. Loud once rather than a silent drop — silence here reads as "the grant did not work" and sends the
        // reader to the grant table instead of to the guest's channel declarations.
        if (addon.ResponseChannel < 0) {
            if (!addon.UndeliverableReported) {
                addon.UndeliverableReported = true;
                Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} declares no response channel (first seen at tick {tick}) — every verdict and every grant disclosure is undeliverable and is dropped, so it can never learn a handle and can never reach a body; declare a response channel beside its request channel]");
            }

            addon.AnswerCount = 0;

            return;
        }

        var budget = (addon.Instance.InputCellCapacity - 1);

        // THE RESERVATION, realized: this tick's protected mutation-answer cells install FIRST, before
        // EmitDisclosures/MergeAnswers ever run — they may never consume a reserved cell. Each slot's Verdict is
        // whatever ResolveMutations/CompleteMutation already decided (a stage 1-5 refusal decided at decode time, or
        // Applied/Rejected decided at drain); AddonVerdict.None can only mean a decode-time-enqueued act whose drain
        // has not yet run, which is unreachable here — DrainPendingOps always runs before ResolveReads within one
        // Step (see WorldServer.Step's own pinned order).
        for (var index = 0; (index < addon.ReservedCount); ++index) {
            addon.Pending[addon.PendingCount++] = (addon.ReservedAnswers[index] with { Channel = ((byte)addon.ResponseChannel) });
        }

        // EmitDisclosures' own "does it fit" check compares its count against a bare budget number (it does not
        // look at PendingCount), so it receives the budget ALREADY REDUCED by the reservation above. MergeAnswers
        // computes its own remaining room as (budget - addon.PendingCount) dynamically, and PendingCount already
        // reflects both the reservation and whatever EmitDisclosures just added by the time it runs — so it
        // receives the FULL budget, unreduced, and the two calls agree on how much room is actually left.
        EmitDisclosures(
            addon: addon,
            budget: (budget - addon.ReservedCount)
        );
        // World events (four families) plus this guest's own machine-memory watches (the fifth), within
        // whatever ring room remains after reservations/disclosures — see EmitEvents' own remarks for the
        // overflow doctrine (ordered prefix, drop-newest, per-mount gap counter).
        EmitEvents(
            addon: addon,
            budget: budget
        );
        ResolveAsks(addon: addon);
        ResolveQueries(addon: addon);
        MergeAnswers(
            addon: addon,
            budget: budget
        );
        addon.AnswerCount = 0;
    }
    // Submit one body's folded intent under the addon's own principal, recording the outcome so the acts that fed it can
    // be answered. A denial is not reported here — WorldServer.ApplyIntentSubmission already prints it once per denial
    // episode, attributed to the body that lost its grant.
    private void Submit(MountedAddon addon, int slot, ulong tick) {
        ref var contribution = ref addon.Contributions[slot];
        var bodyIndex = contribution.BodyIndex;

        if (m_server.Body(index: bodyIndex) is not { } body) {
            contribution.Outcome = AddonVerdict.NoSuchSubject;

            return;
        }

        var submission = new IntentSubmission(
            Tick: tick,
            EntityIndex: bodyIndex,
            Intent: RoleChannels(
                values: contribution.Values,
                channels: m_server.Population.Channels
            ),
            Principal: addon.Principal,
            // The same split as SeatController: movement roles ride Intent; composition ordinals ride the held-device
            // image. The held image overlays a tape-driven body, so a guest's press reaches it like a human's held
            // button; WorldBody.Advance consumes it after one tick.
            HeldChannels: CompositionChannels(
                values: contribution.Values,
                channels: m_server.Population.Channels
            )
        );
        var verdict = m_server.ApplyIntentSubmission(
            body: body,
            submission: in submission
        );

        if (!verdict.IsAllowed) {
            contribution.Outcome = WorldAddonWire.FromRule(rule: verdict.Rule);

            return;
        }

        contribution.Outcome = AddonVerdict.None;

        // Nudge a granted body Live the first tick it is not, mirroring a fresh seat's own default so a newly-granted
        // addon does not sit waiting on a wander/idle producer to yield. Applied DIRECTLY, never through the loopback:
        // this is re-derived by re-running the guest under replay's re-run posture, so it must never be recorded as
        // server input. ApplyCommand re-checks Drive itself — a handle designates, it never decides.
        //
        // ApplyIntentSubmission's ALLOWED verdict answers only "does this principal hold Drive reach over the body" —
        // on a HUMAN-OCCUPIED body that says nothing about whether the fold actually accepted anything from this
        // addon: StageContribution still refuses a channel this document-mounted addon never declared Reach over,
        // silently, and a submission that clears every ordinal that way must not be allowed to cancel the seat's own
        // Idle/Wander/Attend control. So the nudge is gated a second time, narrower than Drive authority: an
        // UNOCCUPIED body is nudged exactly as before (a bot at full authority); a HUMAN-OCCUPIED body is nudged
        // only when this contribution actually reached its OWN declared Reach on at least one channel.
        if (
            (body.Source != IntentSource.Live) &&
            (!m_server.Population.IsHumanOccupied(bodyIndex: bodyIndex) || ContributionAccepted(
            bodyIndex: bodyIndex,
            principal: addon.Principal,
            values: in contribution.Values
        ))
        ) {
            m_server.ApplyCommand(command: new WorldCommand.SetControl(
                Principal: addon.Principal,
                EntityIndex: bodyIndex,
                Source: IntentSource.Live
            ));
        }
    }

    /// <summary>Resolves each staged input act through the guest's own Drive handle table — pump point 2, after the
    /// intent drain and before the population advances — folds the acts into one <see cref="PlayerIntent"/> per
    /// contributed body, and submits each through the same authority path a seat's submission runs
    /// (<see cref="WorldServer.ApplyIntentSubmission"/>). <b>What happens next to a body a seat co-drives is no longer a
    /// plain overwrite</b> (<see cref="FixedContributionFold"/>): on a human-occupied body the
    /// submission routes into that tick's per-body contribution set instead — both halves of it, the intent and the
    /// held-channel composition image — bounded by this guest's own declared reach
    /// (<see cref="WorldGrant.Reach"/>) and by the ceiling the occupying seat authored per channel on its own
    /// row (<see cref="WorldGrant.Ceiling"/>), and folded with the seat's own value by
    /// <see cref="WorldServer"/>'s own channel-contribution fold — never tracked as contention, because a consented (or
    /// default-denied) contribution is the feature, not a race. An unoccupied body is untouched: it still applies
    /// exactly as this paragraph used to describe for every body, contention reporting included, because occupancy is
    /// what makes a pool exist at all. Every channel — movement role and composition alike — is folded fresh from this
    /// tick's acts only: the host holds no cross-tick channel state, so a guest that stops acting on a body simply
    /// stops contributing to it, the same way a seat's analog clear works.</summary>
    /// <param name="tick">The tick the submissions are for.</param>
    public void ApplyContributions(ulong tick) {
        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];

            if (addon.Instance.State != AddonState.Enabled) {
                // FoldActs did not run this tick, so its own bottom-of-method latch reset did not run either — clear
                // it here so a guest disabled or faulted between an exhaustion and its next FoldActs call does not
                // carry an armed-but-orphaned latch into a later enable and swallow the next real exhaustion.
                addon.DriveDispatchBudgetExhaustedReported = false;

                continue;
            }

            FoldActs(
                addon: addon,
                tick: tick
            );
        }
    }
    /// <summary>Resolves each guest's disclosures, world-event pushes, and queued asks/pose queries — pump point 3,
    /// after the population advances and before the snapshot is emitted. This is the pinned
    /// drain point: a verdict, a minted handle, and a pose all reflect the grant table and the authoritative state as of
    /// the step of the tick the record was written in. Disclosures are pushed first (the guest's bootstrap — enumeration
    /// is itself a capability, so a guest cannot know a body index until the host hands it one), then world events
    /// (four families plus the guest's own machine-memory watches), then asks and pose queries are answered, and the
    /// whole result is budgeted into the guest's input batch for the next tick.</summary>
    /// <param name="tick">The tick whose reads are being resolved.</param>
    public void ResolveReads(ulong tick) {
        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];

            if (addon.Instance.State != AddonState.Enabled) {
                // StageBatch — and therefore ResolveQueries and MergeAnswers — did not run this tick, so neither
                // latch's own bottom-of-method reset ran either. Same reasoning as ApplyContributions' twin above,
                // for both episodes this stage owns: a guest that stops being pumped has no open episode left to
                // report on, on either axis, regardless of why it stopped.
                addon.DispatchBudgetExhaustedReported = false;
                addon.QuotaDropReported = false;

                continue;
            }

            StageBatch(
                addon: addon,
                tick: tick
            );
        }
    }
    /// <summary>Composes each guest's input batch (the tick cell, then the disclosures and answers staged at the end
    /// of the previous tick), runs <c>puck_on_tick</c>, and decodes plus vocabulary-validates the returned batch
    /// through the Simulation adapter — pump point 1, the top of <see cref="WorldServer.Step"/>, before the
    /// pending-edit and intent drains. Nothing is applied here: a validated batch is only staged, so a guest's pose
    /// reads and its acts both resolve at their own pinned points later in the same tick.</summary>
    /// <param name="tick">The tick the batch reports — the same tick number a seat's submission carries.</param>
    public void TickAddons(ulong tick) {
        // The addon mutation seam's GLOBAL byte meter resets once per Step, before any addon's acts are decoded —
        // see AddonAbi.MaxMutationBytesPerTickAllAddons.
        m_mutationBytesThisTickAllAddons = 0;

        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];
            var instance = addon.Instance;

            if (instance.State != AddonState.Enabled) {
                addon.LastTickFuelConsumed = 0UL;
                ReportFault(addon: addon);

                continue;
            }

            // Enabled-but-unadmitted is a host sequencing state Admit closes during TryPrepare — so it is
            // unreachable through the prepare/commit door. It is kept as a defensive skip, not an armed trap, for
            // any FUTURE caller that reaches AddonHost directly rather than through this runtime (ticking an
            // unadmitted instance throws by contract).
            if (!instance.Admitted) {
                addon.LastTickFuelConsumed = 0UL;

                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: "instance is enabled but was never admitted — skipped every tick (a caller bypassed the prepare/commit door, which admits during preparation)"
                    );
                }

                continue;
            }

            // The boot-anchored replay arm predicate's own latch: an admitted execution is about to be ATTEMPTED,
            // unconditionally, regardless of what the tick below does — see MountedAddon.HasEverPumped's own doc.
            addon.HasEverPumped = true;

            var batch = addon.Batch;

            batch[0] = new AddonInCell(
                A: ((long)tick),
                B: 0L,
                Channel: 0,
                HandleGeneration: 0,
                HandleIndex: 0,
                Kind: AddonInCellKind.Tick,
                Ordinal: 0,
                Verb: 0,
                Verdict: AddonVerdict.None
            );

            for (var pending = 0; (pending < addon.PendingCount); ++pending) {
                batch[(pending + 1)] = addon.Pending[pending];
            }

            // Guaranteed within the guest's declared capacity: ResolveReads budgets the pending buffer to capacity - 1,
            // and the tick cell is the one this reserves.
            var count = (addon.PendingCount + 1);

            addon.PendingCount = 0;

            var pumped = addon.Pump.Pump(
                instance: instance,
                input: batch.AsSpan(
                    length: count,
                    start: 0
                )
            );

            // Fuel spent THIS tick, whether the tick succeeded or trapped (a trap that burns the whole budget before
            // faulting is the spinning-guest case an operator needs to see) — read from the pump, the one crossing
            // that already reads the tick result. Accumulated into the running total saturating rather than
            // wrapping: a document may admit per-tick fuel up to long.MaxValue, so faulting ticks could otherwise
            // overflow the ulong total and run it backwards.
            var fuelConsumedThisTick = addon.Pump.FuelConsumed;

            addon.LastTickFuelConsumed = fuelConsumedThisTick;
            addon.TotalFuelConsumed = ((addon.TotalFuelConsumed > (ulong.MaxValue - fuelConsumedThisTick))
                ? ulong.MaxValue
                : (addon.TotalFuelConsumed + fuelConsumedThisTick)
            );

            if (!pumped) {
                // The pump returns false only when the instance faulted (a trap, or a whole-batch vocabulary refusal);
                // neither prints anything of its own, so the attribution belongs here.
                ReportFault(addon: addon);

                addon.ReservedCount = 0;

                continue;
            }

            // The addon mutation seam's I1: SubmitMutation acts in THIS batch are decoded and dispatch-gated
            // (stages 1-5 of the six-stage door) right here, at whole-batch decode time — before EmitDisclosures/
            // MergeAnswers (pump point 3) ever see the remaining answer budget, and before DrainPendingOps (later
            // in this SAME Step, before intents) applies whatever cleared the door.
            ResolveMutations(
                addon: addon,
                tick: tick
            );
        }
    }

    // One body's accumulating contribution for a tick — the world's channel VECTOR, every ordinal declarative and
    // per-tick, the host holding no state across ticks for any of them. Values is opened fresh per (tick, body) by
    // Contribution and written by ordinal in Fold (deterministic FixedQ4816 throughout — no float ever crosses),
    // then split at submission: movement roles become Intent and composition ordinals become IntentSubmission's
    // one-tick held-channel overlay (WorldBody.Advance clears the image it consumed, so no host-side release
    // bookkeeping is needed here — the body's own one-tick contract already provides it). Outcome is filled by Submit
    // and read by the answering pass, with None meaning "allowed, answer nothing".
    private struct BodyContribution(int bodyIndex) {
        public int BodyIndex = bodyIndex;
        public ChannelValues Values = default;
        public AddonVerdict Outcome = AddonVerdict.None;
    }
}
