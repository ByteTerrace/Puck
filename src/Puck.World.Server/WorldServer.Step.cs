using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool m_ruleStatePreflightRejected;
    /// <summary>Observes a music segment transition the instant it commits (the same tick <c>MusicDirector</c>
    /// records it, from the music-step call site in <see cref="StepCore"/>) — mirroring
    /// <see cref="SaveEffectTap"/>/<see cref="WorldMachineHost.MachineLifecycleTap"/>'s "the server calls out, the
    /// composition root supplies the capability" shape: this project references no audio director, so it cannot fire
    /// the <c>music.transition</c> cue itself. Carries nothing but the tick — the committed segment ids are already
    /// re-derivable from <c>MusicDirector.LastTransitionFromSegmentId</c>/<c>LastTransitionToSegmentId</c>, so no
    /// second value need round-trip through the tap. A <see langword="null"/> tap is a silent no-op, the same
    /// convention every other tap here follows; every live boot shape wires one (<c>WorldPostBuildWiring.Install</c>).
    /// Never taped — see <c>MusicJudgeReplayReDerivabilityLawTests</c>: the director's own state is purely
    /// re-derivable from the document plus tick, so a fresh replay boot re-fires the identical sequence of
    /// invocations without a recorded entry.</summary>
    public Action<ulong>? MusicTransitionTap { get; set; }
    /// <summary>Observes the active conditional-layer set the instant it CHANGES tick over tick (the same music-step
    /// call site <see cref="MusicTransitionTap"/> fires from) — level-triggered, so unlike a transition this can
    /// fire on any tick, not only a commit. Carries the whole new set (never a delta): a layer is level-triggered,
    /// so the composition root's own consumer re-derives from the current set every time regardless. A
    /// <see langword="null"/> tap is a silent no-op; every live boot shape wires one.</summary>
    public Action<IReadOnlyList<string>>? MusicLayerTap { get; set; }
    /// <summary>Observes a director embellishment the instant it fires (the same music-step call site
    /// <see cref="MusicTransitionTap"/> fires from) — carries the patch id, since (unlike a transition) an
    /// embellishment's PATCH is authored per-embellishment and not re-derivable from any fixed cue-table row. A
    /// <see langword="null"/> tap is a silent no-op; every live boot shape wires one.</summary>
    public Action<string>? MusicEmbellishmentTap { get; set; }

    // The active-layer set observed as of the end of the PREVIOUS Step call — MusicLayerTap fires only when this
    // tick's set differs, so a level-triggered layer that stays active for many ticks in a row costs one comparison
    // per tick, not one tap invocation per tick.
    private IReadOnlyList<string> m_lastTappedActiveLayerTuneIds = [];

    // MusicDirector.ActiveLayerTuneIds is recomputed in stable declared order every Step, so an ordinal sequence
    // compare is exact — never a set compare, which would treat a reorder as a no-op.
    private static bool ActiveLayerSetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) {
        if (a.Count != b.Count) {
            return false;
        }

        for (var index = 0; (index < a.Count); index++) {
            if (!string.Equals(a: a[index], b: b[index], comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }
    private void DispatchServerEvent(WorldServerEvent serverEvent, bool ordered) {
        if (ordered) {
            EnqueueOrdered(entry: new OrderedEntry.ServerEvent(Value: serverEvent));
        } else {
            ApplyServerEvent(serverEvent: serverEvent);
        }
    }
    // Drains the ordered domain FIFO until empty, applying each envelope through the same per-kind apply methods the
    // per-kind IServerLink surface called directly, and invoking that entry's completion with the typed result.
    // Callers hold the authority gate. The reentrancy guard therefore only ever sees this thread's own drain: a
    // re-entrant Submit-from-inside-an-apply re-enqueues and returns to the outer drain's loop instead of recursing.
    private void DrainOrdered() {
        if (m_drainingOrdered) {
            return;
        }

        m_drainingOrdered = true;

        try {
            while (m_ordered.TryDequeue(result: out var entry)) {
                switch (entry) {
                    case OrderedEntry.Submission submission:
                        var result = ApplyEnvelope(envelope: submission.Envelope);

                        submission.Completion?.Invoke(obj: result);
                        break;
                    case OrderedEntry.ServerEvent serverEvent:
                        ApplyServerEvent(serverEvent: serverEvent.Value);
                        break;
                }
            }
        } finally {
            m_drainingOrdered = false;
        }
    }
    // Drain every buffered live edit in FIFO order, applying it at this tick boundary. Delivers the new definition to
    // the client sink ONCE if at least one edit applied (once per step with >=1 applied edit, not once per edit).
    private bool DrainPendingOps(ulong tick) {
        var applied = false;

        while (m_pending.TryDequeue(result: out var op)) {
            var ok = op switch {
                // An addon-sourced op was already metered at the seam's pre-flight (before decode, deliberately), so
                // re-entering the budget gate here would charge one guest dispatch twice against the same tick's
                // allowance. Every other source — console, loopback, a peer's submission — is metered right here.
                PendingOp.Mutate mutate => TryApplyMutation(
                mutation: mutate.Mutation,
                tick: tick,
                connectionId: mutate.ConnectionId,
                correlationId: mutate.CorrelationId,
                preMetered: (mutate.SourceAddonInstanceId >= 0L)
            ),
                PendingOp.Rebuild rebuild => ApplyRebuild(
                request: rebuild.Request,
                principal: rebuild.Principal,
                connectionId: rebuild.ConnectionId,
                correlationId: rebuild.CorrelationId,
                expectedContentHash: rebuild.ExpectedContentHash,
                preparationFailure: rebuild.PreparationFailure
            ),
                PendingOp.Undo undo => ApplyUndo(
                count: undo.Count,
                principal: undo.Principal,
                connectionId: undo.ConnectionId,
                correlationId: undo.CorrelationId
            ),
                _ => false,
            };

            // The tape's own completion field: fires exactly once, for exactly the ops ApplyEnvelope's own dispatch
            // threaded one onto — see EnqueueMutation's own remarks.
            if (op is PendingOp.Mutate { OutcomeObserved: { } outcomeObserved }) {
                outcomeObserved(obj: ok);
            }

            // The addon mutation seam's I2: an addon-sourced Mutate op's OUTCOME — never its application, which
            // just ran above through the identical machinery a console mutation runs through — routes back to the
            // originating guest's RESERVED answer cell here, at drain time (same Step, before intents). The cell
            // itself is not delivered until ResolveReads(T) stages it into the guest's batch T+1; this only records
            // which verdict that staging will use. A well-formed mutation the document-apply pipeline itself
            // refused (a validation/capacity/cross-row failure — TryApplyMutation already printed the loud reason)
            // answers Rejected, distinct from every dispatch-door refusal the seam's earlier stages produce.
            if ((op is PendingOp.Mutate { SourceAddonInstanceId: >= 0L } addonMutate)) {
                m_addons?.CompleteMutation(
                    addonInstanceId: addonMutate.SourceAddonInstanceId,
                    actOrdinal: addonMutate.ActOrdinal,
                    applied: ok
                );
            }

            applied |= ok;
        }

        if (applied) {
            m_output.DeliverDefinition(definition: m_definition);
        }

        return applied;
    }
    // Build and deliver the tick's snapshot to every typed-lane subscriber. Skipped with no subscriber attached.
    private void EmitSnapshot(ulong tick, ulong stepTicks) {
        if (!m_output.HasTypedSubscribers) {
            return;
        }

        m_output.DeliverSnapshot(snapshot: BuildSnapshot(
            stepTicks: stepTicks,
            tick: tick
        ));
    }
    // The one door into the ordered domain. The authority gate is held across both the enqueue and the drain, which
    // is what makes m_ordered and m_drainingOrdered single-threaded state rather than shared state: a tick-thread
    // Submit and a socket worker's gated authority operation both reach this queue, and a drain skipped because a
    // different thread held the guard would leave an already-applied population change (an admitted arrival)
    // standing without the grant rows its own event carries. lock is reentrant, so an authority operation that
    // dispatches from inside the gate re-enters here without deadlocking.
    private void EnqueueOrdered(OrderedEntry entry) {
        lock (m_authorityGate) {
            m_ordered.Enqueue(item: entry);
            DrainOrdered();
        }
    }
    // The rule/interaction ARRAY is snapshotted first (the caller's own m_rules/m_interactions read), which is a
    // different thing from the state the gates read: a rule's own effect installs a new definition, which reassigns
    // m_rules/m_interactions — and iterating a field an inner call reassigns is how a rule would silently stop seeing
    // its siblings mid-tick. Every row declared at the top of the tick evaluates during this tick; a row ADDED by
    // this tick's effects starts on the next one, the same next-tick boundary every other mutation already lands on.
    private bool EvaluateCompiledRules(CompiledWorldRule[] rules, RuleLatch latch, ulong tick, ulong stepTicks) {
        var applied = false;

        if (rules.Length == 0) {
            return applied;
        }

        foreach (var rule in rules) {
            if (rule.Decision is not null) {
                applied |= EvaluateDecisionRule(rule, tick, stepTicks);
                continue;
            }
            var bindings = latch.Bindings(name: rule.Name);

            if (rule.Interaction is { } interaction) {
                applied |= EvaluateInteraction(
                    bindings: bindings,
                    interaction: interaction,
                    latch: latch,
                    rule: rule,
                    stepTicks: stepTicks,
                    tick: tick
                );

                continue;
            }

            if (rule.ForEach is { } forEach) {
                // The keys are snapshotted before the first evaluation, so an effect minting a cell (a status
                // applied to a new carrier) starts ticking next tick, never mid-iteration.
                EachKeys(
                    into: m_eachKeyScratch,
                    row: forEach
                );
                latch.BeginSweep();

                for (var position = 0; position < m_eachKeyScratch.Count; position++) {
                    var key = m_eachKeyScratch[position];
                    var numeric = WorldStateReader.TryParseCandidateIndex(index: out var index, key: key);
                    m_boundEach = numeric ? index : -1;
                    m_boundEachKey = key.Value;
                    applied |= EvaluateOnce(
                        latch: latch,
                        binding: new LatchKey(
                            Left: numeric ? index : (PositionalLatchBase | position),
                            Right: -1
                        ),
                        bindings: bindings,
                        rule: rule,
                        stepTicks: stepTicks,
                        tick: tick
                    );
                }

                m_boundEach = -1;
                m_boundEachKey = null;
                latch.EndSweep(bindings: bindings);

                continue;
            }

            applied |= EvaluateOnce(
                binding: LatchKey.None,
                bindings: bindings,
                latch: latch,
                rule: rule,
                stepTicks: stepTicks,
                tick: tick
            );
        }

        return applied;
    }
    // One gate-and-fire under the bindings already in place. EDGE fires on the CROSSING alone and re-arms only when
    // the gate closes again; LEVEL fires every tick the gate holds — one vocabulary, the same ActionTriggerMode a
    // per-body fact trigger reads. The latch is per evaluation binding (a bound body or pair), never per rule alone;
    // a bound entry that is not evaluated this tick (the pair left range, the carrier lost its tag or despawned) is
    // closed by the enclosing sweep, which is what re-arms an Edge interaction whose synthesized gate is always open.
    private bool EvaluateOnce(CompiledWorldRule rule, RuleLatch latch, Dictionary<LatchKey, bool> bindings, LatchKey binding, ulong tick, ulong stepTicks) {
        var open = RuleGateOpen(
            gate: rule.Gate,
            tick: tick
        );
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: bindings,
            exists: out _,
            key: binding
        );
        var wasOpen = slot;

        slot = open;
        latch.Touch(binding: binding);

        if (
            !open ||
            ((rule.Mode == ActionTriggerMode.Edge) && wasOpen)
        ) {
            return false;
        }

        return FireWorldRuleEffects(effects: rule.Effects, ruleName: rule.Name, tick: tick, stepTicks: stepTicks);
    }
    private static bool IsAtomicStateEffect(WorldRuleEffectKind kind) => kind is
        WorldRuleEffectKind.Write or WorldRuleEffectKind.Countdown or WorldRuleEffectKind.RemoveStateCell or WorldRuleEffectKind.ScheduleState;
    private bool FireWorldRuleEffects(CompiledWorldEffect[] effects, string ruleName, ulong tick, ulong stepTicks) {
        var applied = false;

        for (var index = 0; index < effects.Length;) {
            if (IsAtomicStateEffect(kind: effects[index].Kind)) {
                var end = (index + 1);

                while ((end < effects.Length) && IsAtomicStateEffect(kind: effects[end].Kind)) {
                    end++;
                }

                // A single effect performs its own refusal-suppressing preflight. A longer run preflights as one
                // candidate so no earlier write leaks when a later one refuses.
                var preflighted = ((end - index) > 1);
                if (!preflighted || PreflightWorldRuleStateEffects(effects: effects, ruleName: ruleName, start: index, end: end, tick: tick, stepTicks: stepTicks)) {
                    for (; index < end; index++) {
                        applied |= FireWorldRuleEffect(effect: effects[index], ruleName: ruleName, stepTicks: stepTicks, tick: tick, strict: preflighted);
                    }
                } else {
                    index = end;
                }

                continue;
            }

            applied |= FireWorldRuleEffect(effect: effects[index], ruleName: ruleName, stepTicks: stepTicks, tick: tick);
            index++;
        }

        return applied;
    }
    // Every (left carrier, right carrier) pair within range, or every left carrier inside the region, fires the
    // interaction once with left/right bound — the chemistry is evaluated over all carriers, never one argmax pair.
    private bool EvaluateInteraction(CompiledWorldRule rule, CompiledInteraction interaction, RuleLatch latch, Dictionary<LatchKey, bool> bindings, ulong tick, ulong stepTicks) {
        var applied = false;
        var lefts = m_carrierScratchLeft;

        Carriers(
            into: lefts,
            row: interaction.Left,
            tick: tick
        );
        latch.BeginSweep();

        if (interaction.CoOccurrence == WorldInteractionCoOccurrence.Region) {
            foreach (var left in lefts) {
                if (!m_events.IsOccupant(
                    body: left,
                    placementId: interaction.Right
                )) {
                    continue;
                }

                m_boundLeft = left;
                m_boundRight = -1;
                applied |= EvaluateOnce(
                    latch: latch,
                    binding: new LatchKey(
                        Left: left,
                        Right: -1
                    ),
                    bindings: bindings,
                    rule: rule,
                    stepTicks: stepTicks,
                    tick: tick
                );
            }
        } else {
            var rights = m_carrierScratchRight;
            // Range is a finite non-negative authored distance; its square only leaves the carrier past 2^39 raw,
            // where every saturated LengthSquared already compares within it.
            var rangeSquared = ((interaction.Range.Value < (1L << 39))
                ? (interaction.Range * interaction.Range)
                : FixedQ4816.MaxValue
            );

            Carriers(
                into: rights,
                row: interaction.Right,
                tick: tick
            );

            foreach (var left in lefts) {
                foreach (var right in rights) {
                    // A carrier an earlier pair's effect despawned mid-sweep reads the sentinel, never a distance.
                    var distanceSquared = ReadBodyDistanceSquared(
                        bodyA: left,
                        bodyB: right
                    );

                    if (
                        (right == left) ||
                        (distanceSquared == NoBodyDistance) ||
                        (distanceSquared > rangeSquared)
                    ) {
                        continue;
                    }

                    m_boundLeft = left;
                    m_boundRight = right;
                    applied |= EvaluateOnce(
                        latch: latch,
                        binding: new LatchKey(
                            Left: left,
                            Right: right
                        ),
                        bindings: bindings,
                        rule: rule,
                        stepTicks: stepTicks,
                        tick: tick
                    );
                }
            }
        }

        m_boundLeft = -1;
        m_boundRight = -1;
        latch.EndSweep(bindings: bindings);

        return applied;
    }
    // The integer keys a keyed row holds at this moment, ascending — the iteration set of a forEach rule. Fills the
    // caller's scratch list; the cells themselves are not retained.
    // A latch key for a non-integer forEach cell: its position in the row, flagged above any body index.
    private const int PositionalLatchBase = 0x4000_0000;
    private readonly List<WorldCellName> m_eachKeyScratch = [];
    private string? m_boundEachKey;

    // Every cell key of the iterated row in cell order; an integer key also binds the body of that index.
    private void EachKeys(string row, List<WorldCellName> into) {
        into.Clear();

        if (WorldDefinitionRows.FindStateRow(
            rows: m_definition.State,
            name: row
        ) is { Cells: { } cells }) {
            for (var index = 0; index < cells.Count; index++) {
                into.Add(item: cells[index].Key);
            }
        }
    }

    private void CarrierKeys(string row, List<int> into) {
        into.Clear();

        if (WorldDefinitionRows.FindStateRow(
            rows: m_definition.State,
            name: row
        ) is { Cells: { } cells }) {
            for (var index = 0; index < cells.Count; index++) {
                var cell = cells[index];
                if (WorldStateReader.TryParseCandidateIndex(
                    index: out var key,
                    key: cell.Key
                )) {
                    into.Add(item: key);
                }
            }
        }

        into.Sort();
    }
    // The active bodies whose cell in a keyed tag row reads nonzero, ascending, into the caller's scratch list. A
    // plain cell's stored value is its live value; only an advancing cell goes through the reader's as-of-tick walk.
    private void Carriers(string row, ulong tick, List<int> into) {
        into.Clear();

        if (WorldDefinitionRows.FindStateRow(
            rows: m_definition.State,
            name: row
        ) is not { Cells: { } cells }) {
            return;
        }

        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++) {
            var cell = cells[cellIndex];
            if (
                !WorldStateReader.TryParseCandidateIndex(
                index: out var index,
                key: cell.Key
            ) ||
                (Body(index: index) is null)
            ) {
                continue;
            }

            var nonzero = (((cell.Advance is null) && (cell.Cycle is null))
                ? (cell.Value != 0L)
                : (ReadStateCell(
                    row: row,
                    key: cell.Key,
                    tick: tick
                ) != FixedQ4816.Zero)
            );

            if (nonzero) {
                into.Add(item: index);
            }
        }

        into.Sort();
    }
    // Evaluates every compiled rule's gate and fires its effects, in DOCUMENT ORDER — then every compiled
    // INTERACTION's, same terms, AFTER every rule. That ordering (rules, then interactions, each internally in
    // document order) IS the same-tick effect tiebreak this pair documents: a rule can set up a fact an interaction's
    // gate reads THIS tick, and two interactions cascade in their own declared order (interaction A tags a carrier
    // interaction B's gate then reads) on the identical terms a rule chain already does.
    //
    // Effects apply IMMEDIATELY, not at a boundary: FireWorldRuleEffect calls TryApplyMutation, which installs the
    // composed definition on the spot. So a later rule's gate DOES read an earlier rule's same-tick write — and so
    // does a later effect's live 'from' operand, which reads through the same ReadWorldFact walk. The rules in one
    // tick are a sequence, not a simultaneous snapshot, and a chain (rule A sets a flag, rule B gates on it, rule C
    // copies it) fires end to end within one tick. That is deterministic because document order is: the same
    // document and the same input produce the same sequence on every run, machine, and backend.
    //
    // Effects install through TryApplyMutation directly, bypassing the pending-op queue and its per-step
    // DeliverDefinition, so the delivery happens here: once per tick with at least one applied effect, the same
    // once-per-step shape DrainPendingOps keeps. KEEP IN SYNC with DrainPendingOps' delivery.
    private void EvaluateWorldRules(ulong tick, ulong stepTicks) {
        m_decisionWork = default;
        FreezeDecisionPerception(m_rules);
        var applied = EvaluateCompiledRules(
            latch: m_ruleGateHeld,
            rules: m_rules,
            stepTicks: stepTicks,
            tick: tick
        );

        applied |= EvaluateCompiledRules(
            latch: m_interactionGateHeld,
            rules: m_interactions,
            stepTicks: stepTicks,
            tick: tick
        );

        if (applied) {
            m_output.DeliverDefinition(definition: m_definition);
        }
    }
    // Submits the effect's own ORDINARY mutation through the ordinary pipeline (admission → compose → whole-document
    // validate → install → journal → echo), stamped WorldPrincipal.World — the SAME door UpsertHudPanel/RemoveHudPanel
    // /UpsertPlacement/RemovePlacement already have from the console or an addon; nothing here is a new admission
    // path. A WRITE THAT CANNOT MOVE THE DESTINATION is skipped before submission — either the resolved value already
    // matches the cell, or the row's declared envelope pins the cell where it is (see the Write arm): a
    // level-triggered gate re-fires every tick it holds, and without this a standing rule would append an identical
    // journal entry forever, or draw an identical refusal forever. A GENERATE is never a no-op — it advances the
    // generator's cursor by construction — and neither is a HUD/placement upsert or remove, so both are submitted
    // (the one exception being a removePlacement on a possessed carrier, which the CarrierPossessed guard below skips
    // outright rather than submitting). SAVE is the one exception to all of this: it submits no WorldMutation at all (see
    // ActionEffect.Save's remarks) and is handled before the mutation switch below ever runs.
    private bool FireWorldRuleEffect(CompiledWorldEffect effect, string ruleName, ulong tick, ulong stepTicks, bool preflight = false, bool strict = false) {
        if (effect.Kind == WorldRuleEffectKind.TransformState) {
            return ApplyWorldRuleMutation(effect: in effect, ruleName: ruleName, mutation: new WorldMutation.TransformState(WorldPrincipal.World, effect.Transform!), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false, preflight: preflight);
        }
        if (effect.Kind == WorldRuleEffectKind.Transaction) {
            return FireWorldRuleTransaction(transaction: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks);
        }
        if (effect.Kind == WorldRuleEffectKind.PushState) {
            return FirePushState(effect: in effect, ruleName: ruleName, tick: tick, preflight: preflight);
        }
        if (effect.Kind == WorldRuleEffectKind.EmitCue) {
            if (!preflight) {
                FireGameplayCue(effect: effect, tick: tick);
            }
            return false;
        }
        if (effect.Kind == WorldRuleEffectKind.Body) {
            FireBodyEffect(effect: effect, ruleName: ruleName, tick: tick, preflight: preflight);
            return false;
        }
        if (effect.Kind == WorldRuleEffectKind.PaintField) {
            FireFieldPaint(effect: effect, ruleName: ruleName, tick: tick, preflight: preflight);
            return false;
        }
        if (effect.Kind == WorldRuleEffectKind.Save) {
            if (preflight) {
                m_ruleStatePreflightRejected = true;
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.SaveUnavailable, ruleName: ruleName, effect: in effect, tick: tick, detail: "save effects are not atomic transaction steps");
                return false;
            }
            // No compose, no validate, no install, no journal — SaveEffectTap performs the identical settle-at-save
            // capture 'world.save' itself runs, straight to the world's own loaded file. A null tap (no composition
            // root wired) is a silent no-op, the same convention EchoTap follows.
            if (SaveEffectTap is { } save) {
                save(tick);
            } else {
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.SaveUnavailable, ruleName: ruleName, effect: in effect, tick: tick, detail: "no save-effect host is attached");
            }

            return false;
        }

        if (effect.Kind == WorldRuleEffectKind.Pose) {
            FirePoseEffect(
                effect: effect,
                ruleName: ruleName,
                tick: tick,
                preflight: preflight
            );

            return false;
        }

        // An ordinary rule mutation is preflighted before it reaches the loud mutation door. This turns a standing
        // Level-rule refusal into one bounded structured diagnostic instead of one rejection line per tick. A
        // transaction/contiguous-run commit passes strict=true because its whole branch was already preflighted.
        if (!preflight && !strict) {
            var installed = m_definition;
            m_ruleStatePreflightRejected = false;
            try {
                _ = FireWorldRuleEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: true, strict: true);
            } finally {
                m_definition = installed;
            }
            if (m_ruleStatePreflightRejected) {
                m_ruleStatePreflightRejected = false;
                return false;
            }
        }

        // A '$cell:' destination resolves its key fresh every firing, exactly as a gate operand's does.
        var destinationKey = ResolveOperandKey(
            key: effect.Key,
            keyFrom: effect.KeyFrom,
            tick: tick
        );

        if (effect.Kind == WorldRuleEffectKind.RemoveStateCell) {
            if (WorldStateReader.TryRead(
                definition: m_definition,
                rowName: effect.Row,
                key: destinationKey,
                tick: tick,
                row: out _,
                rawValue: out var existing,
                text: out var existingText
            ) && (existing is null) && (existingText is null)) {
                if (!strict) {
                    return false;
                }
            }

            return ApplyWorldRuleMutation(
                effect: in effect,
                ruleName: ruleName,
                mutation: new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.World, Row: effect.Row, Key: destinationKey),
                tick: tick,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                preMetered: false,
                preflight: preflight
            );
        }

        if (effect.Kind is WorldRuleEffectKind.Write or WorldRuleEffectKind.Countdown or WorldRuleEffectKind.ScheduleState) {
            // The destination's CURRENT value through the same shared resolver the gate read: an absent cell reads as
            // zero (an Add mints it), an absent ROW is nothing to write. On an ADVANCING row that is the LIVE value,
            // not the stored base, which is what the could-this-move skip below needs: a base is a fixed point of
            // its own accumulation, so comparing against it would call a write "no-op" whenever the base already
            // happened to match — silently skipping the write, and with it the rebase that is the only way a rule
            // can reset an advancing row at all.
            if (!WorldStateReader.TryRead(
                definition: m_definition,
                rowName: effect.Row,
                key: destinationKey,
                tick: tick,
                row: out var row,
                rawValue: out var destination,
                text: out var currentText
            )) {
                return false;
            }

            if (row.Kind == CellKind.Text) {
                var nextText = effect.Text;

                if (
                    (nextText is null) &&
                    (effect.From is { Kind: WorldRuleFactKind.StateCell } source)
                ) {
                    if (!WorldStateReader.TryRead(
                        definition: m_definition,
                        rowName: source.Row!,
                        key: ResolveOperandKey(
                            key: source.Key,
                            keyFrom: source.KeyFrom,
                            tick: tick
                        ),
                        tick: tick,
                        row: out _,
                        rawValue: out _,
                        text: out nextText
                    )) {
                        return false;
                    }
                }

                if (
                    (nextText is null) ||
                    string.Equals(
                    a: currentText,
                    b: nextText,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return false;
                }

                return ApplyWorldRuleMutation(
                    effect: in effect,
                    ruleName: ruleName,
                    mutation: new WorldMutation.UpsertStateCell(
                        Principal: WorldPrincipal.World,
                        Row: effect.Row,
                        Key: destinationKey,
                        Value: 0L,
                        Kind: WorldDocumentWriteKind.Set,
                        Text: nextText
                    ),
                    tick: tick,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    preMetered: false,
                    preflight: preflight
                );
            }

            var current = (destination ?? 0L);

            // A cycling cell stores its PHASE and reads its rotation; a write moves the phase, so the value an add
            // turns from and the value the could-this-move test compares against is the stored phase, not the live
            // rotation the trait carried it to this tick.
            if (
                WorldCellName.TryParse(candidate: destinationKey, name: out var destinationCell, reason: out _) &&
                (WorldDefinitionRows.FindCell(cells: row.Cells, key: destinationCell) is { } storedCell) &&
                ((storedCell.Cycle is not null) || ((storedCell.Key == WorldStateRow.SlotKey) && (row.Cycle is not null)))
            ) {
                current = storedCell.Value;
            }

            // A live 'from' operand is read fresh EVERY firing (Install swaps m_definition on every apply, so this
            // reads the same settled state a compareState comparand would this tick) and converted to the
            // destination row's own encoding; a literal effect keeps the value the compiler already converted once.
            // A FOREVER fact ($parked: on a forever-parked body) has no number to store — the copy silently does not
            // fire, the same no-narration shape a level gate's own not-holding takes (see ReadParkedRemaining).
            if (
                (effect.Kind != WorldRuleEffectKind.Countdown) &&
                (effect.From is { } foreverProbe) &&
                ReadWorldFact(
                operand: foreverProbe,
                tick: tick
            ).IsForever
            ) {
                return false;
            }

            var expressionFailed = false;
            long raw;

            if (effect.Kind == WorldRuleEffectKind.Countdown) {
                raw = -Math.Min(
                    val1: current,
                    val2: checked((long)stepTicks)
                );
            } else if (effect.Kind == WorldRuleEffectKind.ScheduleState) {
                raw = ScheduleDueTick(tick: tick, delayTicks: effect.RawValue, failed: out expressionFailed);
            } else if (effect.Expression is { } expression) {
                raw = (TryEvaluateExpression(program: expression, kind: row.Kind, tick: tick, value: out var evaluated)
                    ? evaluated
                    : FailedExpression(out expressionFailed)
                );
            } else if (effect.From is { } from) {
                raw = ConvertWorldFactToRaw(value: ReadWorldFact(operand: from, tick: tick), kind: row.Kind);
            } else {
                raw = effect.RawValue;
            }

            if (expressionFailed) {
                if (preflight) {
                    m_ruleStatePreflightRejected = true;
                }
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.Arithmetic, ruleName: ruleName, effect: in effect, tick: tick, detail: "the expression overflowed, divided by zero, or produced an invalid stack result");
                return false;
            }
            var next = ((effect.Write == WorldDocumentWriteKind.Add)
                ? unchecked((current + raw))
                : raw
            );

            // SUBMIT ONLY WHAT COULD MOVE THE DESTINATION. Arithmetic identity is not the whole of that test: a cell
            // already sitting ON a bound its own row declares (NonNegative/Min/Max) cannot be pushed further past it,
            // so a Level gate pointed at a floored row would go on composing a candidate the whole-document validator
            // refuses, once per tick, for the life of the session — the same standing-rule failure the arithmetic
            // check was added for, reached through the row's envelope instead of through its arithmetic.
            //
            // The projection decides WHETHER to submit and never WHAT is submitted: the mutation still carries the
            // rule's own unclamped operand, so a write that genuinely tries to cross a bound (a cell at 3 taking -5)
            // is still submitted and still refused BY NAME. That is the settled envelope duality — a computed value
            // clamps, an explicit write refuses — with the inert case removed from the write side, not softened.
            if (row.ClampToEnvelope(value: next) == current) {
                return false;
            }

            return ApplyWorldRuleMutation(
                effect: in effect,
                ruleName: ruleName,
                mutation: new WorldMutation.UpsertStateCell(
                    Principal: WorldPrincipal.World,
                    Row: effect.Row,
                    Key: destinationKey,
                    Value: raw,
                    Kind: effect.Write
                ),
                tick: tick,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                preMetered: false,
                preflight: preflight
            );
        }

        // DESPAWN-OF-OWNED-CARRIER GUARD (WorldRuleEffectRefusal.CarrierPossessed): a removePlacement targeting a
        // placement whose Inhabit facet is currently bound to a POSSESSED body (a concrete drive grant — see
        // WorldGrants.IsBodyPossessed's own remarks) is skipped rather than fired. This is the widening
        // UpsertPlacement/RemovePlacement's admission into the rule-effect vocabulary was missing: a placement's
        // Inhabit/Region facets already make an ordinary whole-row upsert/remove a BODY/REGION carrier spawn/despawn
        // (WorldPopulation.ReconcileInhabitants reconciles from ANY accepted mutation, principal-agnostic) — this is
        // the one case that must NOT go through silently, because it would destroy an explicit possession grant's
        // binding out from under it (the slot a later, unrelated inhabitant can then claim). OWNER DECISION: REFUSE,
        // never orphan-to-escrow (see the refusal's own remarks for why).
        if (
            (effect.Kind == WorldRuleEffectKind.RemovePlacement) &&
            TryFindPossessedInhabitant(
            placementId: effect.Row,
            bodyIndex: out var possessedBody,
            holder: out var possessor
        )
        ) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.CarrierPossessed, ruleName: ruleName, effect: in effect, tick: tick, detail: $"placement '{effect.Row}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }

            return false;
        }

        WorldMutation mutation = effect.Kind switch {
            WorldRuleEffectKind.Generate => new WorldMutation.Generate(
            Principal: WorldPrincipal.World,
            Row: effect.Row
        ),
            WorldRuleEffectKind.UpsertHudPanel => new WorldMutation.UpsertHudPanel(
            Principal: WorldPrincipal.World,
            Panel: effect.HudPanel!
        ),
            WorldRuleEffectKind.RemoveHudPanel => new WorldMutation.RemoveHudPanel(
            Principal: WorldPrincipal.World,
            Id: effect.Row
        ),
            WorldRuleEffectKind.UpsertPlacement => new WorldMutation.UpsertPlacement(
            Principal: WorldPrincipal.World,
            Placement: effect.Placement!
        ),
            WorldRuleEffectKind.RemovePlacement => new WorldMutation.RemovePlacement(
            Principal: WorldPrincipal.World,
            Id: effect.Row
        ),
            _ => throw new InvalidOperationException(message: $"world rule effect kind '{effect.Kind}' has no fire mapping."),
        };

        return ApplyWorldRuleMutation(
            effect: in effect,
            ruleName: ruleName,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: mutation,
            preMetered: false,
            tick: tick,
            preflight: preflight
        );
    }
    private static long FailedExpression(out bool failed) {
        failed = true;
        return 0L;
    }
    private static long ScheduleDueTick(ulong tick, long delayTicks, out bool failed) {
        failed = (delayTicks < 0L) || (tick > ((ulong)(long.MaxValue - Math.Max(0L, delayTicks))));
        return failed ? 0L : checked(((long)tick) + delayTicks);
    }
    private bool TryEvaluateExpression(CompiledWorldExpressionToken[] program, CellKind kind, ulong tick, out long value) {
        Span<long> stack = stackalloc long[WorldRuleCapacity.MaxExpressionTokens];
        var top = 0;

        try {
            foreach (var token in program) {
                if (token.Operation == WorldExpressionOp.Constant) {
                    stack[top++] = token.Constant;
                    continue;
                }
                if (token.Operation == WorldExpressionOp.Operand) {
                    var fact = ReadWorldFact(operand: token.Operand!.Value, tick: tick);
                    if (fact.IsForever) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = ConvertWorldFactToRaw(value: fact, kind: kind);
                    continue;
                }
                if (token.Operation == WorldExpressionOp.Clamp) {
                    var maximum = stack[--top];
                    var minimum = stack[--top];
                    var input = stack[--top];
                    if (minimum > maximum) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = Math.Clamp(value: input, min: minimum, max: maximum);
                    continue;
                }
                if (token.Operation == WorldExpressionOp.Select) {
                    var whenFalse = stack[--top];
                    var whenTrue = stack[--top];
                    var condition = stack[--top];
                    stack[top++] = ((condition != 0L) ? whenTrue : whenFalse);
                    continue;
                }
                if (token.Operation == WorldExpressionOp.BitField) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var input = stack[--top];
                    if (!WorldExpressionArithmetic.TryBitField(input, offset, width, out var field)) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = field;
                    continue;
                }
                if (token.Operation == WorldExpressionOp.BoardShift) {
                    stack[top - 1] = WorldBoardQueries.ShiftMask(token.Board!, stack[top - 1]);
                    continue;
                }
                if (token.Operation == WorldExpressionOp.BoardImage) {
                    stack[top - 1] = WorldBoardQueries.ImageOfMask(token.Board!.Topology, token.Board.Direction, stack[top - 1]);
                    continue;
                }
                if (token.Operation == WorldExpressionOp.BitInsert) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var field = stack[--top];
                    var input = stack[--top];
                    if (!WorldExpressionArithmetic.TryBitInsert(input, field, offset, width, out var inserted)) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = inserted;
                    continue;
                }
                if (WorldExpressionArithmetic.IsUnary(token.Operation)) {
                    if (!WorldExpressionArithmetic.TryUnary(token.Operation, kind, stack[top - 1], out var unary)) {
                        value = 0L;
                        return false;
                    }
                    stack[top - 1] = unary;
                    continue;
                }

                var right = stack[--top];
                var left = stack[--top];
                // Data-dependent arithmetic refusal can occur thousands of times in a dense flock sample.
                // Preserve the ordinary checked/rounded semantics without allocating an exception per neighbor.
                if (!WorldExpressionArithmetic.TryBinary(token.Operation, kind, left, right, out var result)) {
                    value = 0L;
                    return false;
                }

                stack[top++] = result;
            }
        } catch (ArithmeticException) {
            value = 0L;
            return false;
        }

        value = ((top == 1) ? stack[0] : 0L);
        return (top == 1);
    }
    private bool FireWorldRuleTransaction(CompiledWorldEffect transaction, string ruleName, ulong tick, ulong stepTicks) {
        var effects = transaction.Effects!;
        var installed = m_definition;
        m_ruleStatePreflightRejected = false;

        try {
            for (var index = 0; index < effects.Length; index++) {
                var effect = effects[index];
                _ = FireWorldRuleEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: true, strict: true);
                if (m_ruleStatePreflightRejected) {
                    break;
                }
            }
        } finally {
            m_definition = installed;
        }

        if (m_ruleStatePreflightRejected) {
            m_ruleStatePreflightRejected = false;
            var failure = (transaction.OnFailure ?? []);
            if (failure.Length == 0) {
                return false;
            }

            installed = m_definition;
            try {
                for (var index = 0; index < failure.Length; index++) {
                    var effect = failure[index];
                    _ = FireWorldRuleEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: true, strict: true);
                    if (m_ruleStatePreflightRejected) {
                        return false;
                    }
                }
            } finally {
                m_definition = installed;
                m_ruleStatePreflightRejected = false;
            }

            var failureApplied = false;
            for (var index = 0; index < failure.Length; index++) {
                var effect = failure[index];
                failureApplied |= FireWorldRuleEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, strict: true);
            }
            return failureApplied;
        }

        var applied = false;
        for (var index = 0; index < effects.Length; index++) {
            var effect = effects[index];
            applied |= FireWorldRuleEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, strict: true);
        }
        return applied;
    }
    private void FireGameplayCue(CompiledWorldEffect effect, ulong tick) {
        int? body = null;
        if (effect.Key.Length > 0) {
            var key = ResolveOperandKey(key: effect.Key, keyFrom: effect.KeyFrom, tick: tick);
            if (int.TryParse(s: key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var parsed) && (Body(index: parsed) is not null)) {
                body = parsed;
            }
        }

        var cue = new WorldGameplayCue(Name: effect.Cue!, Payload: effect.Payload, Body: body, Tick: tick);
        GameplayCueTap?.Invoke(obj: cue);
        Console.Error.WriteLine(value: $"[world.cue: {cue.Name} tick={tick}{(body is { } index ? $" body:{index}" : string.Empty)}]");
    }
    private void FireBodyEffect(CompiledWorldEffect effect, string ruleName, ulong tick, bool preflight) {
        var key = ResolveOperandKey(key: effect.Key, keyFrom: effect.KeyFrom, tick: tick);
        if (!int.TryParse(s: key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var bodyIndex) || (Body(index: bodyIndex) is not { } body)) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: in effect, tick: tick, detail: $"body '{key}' is inactive");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }
            return;
        }

        var operation = effect.Body!.Value;
        if (operation.Operation == BodyMotionOp.Designate) {
            if (!m_population.TryResolveTargetRegister(name: operation.Register!, index: out var registerIndex)) {
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: in effect, tick: tick, detail: $"target register '{operation.Register}' is unavailable");
                if (preflight) {
                    m_ruleStatePreflightRejected = true;
                }
                return;
            }
            if (operation.Designation == WorldBodyDesignationKind.Clear) {
                if (!preflight) {
                    m_population.SetDesignation(bodyIndex: bodyIndex, registerIndex: registerIndex, target: WorldTargetDesignation.None);
                }
                return;
            }

            var targetKey = ResolveOperandKey(key: operation.TargetKey, keyFrom: operation.TargetKeyFrom, tick: tick);
            if (!int.TryParse(s: targetKey, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var targetIndex)) {
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: in effect, tick: tick, detail: $"target body key '{targetKey}' is invalid");
                if (preflight) {
                    m_ruleStatePreflightRejected = true;
                }
                return;
            }
            if ((targetIndex == bodyIndex) || (Body(index: targetIndex) is null)) {
                var detail = ((targetIndex == bodyIndex)
                    ? $"body:{bodyIndex} cannot designate itself"
                    : $"target body:{targetIndex} is inactive"
                );
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: in effect, tick: tick, detail: detail);
                if (preflight) {
                    m_ruleStatePreflightRejected = true;
                }
                return;
            }
            if (preflight) {
                return;
            }
            _ = ApplyDesignationCore(
                designation: new WorldDesignation(EntityIndex: bodyIndex, Register: operation.Register!, Subject: GrantSubject.Body(index: targetIndex)),
                principal: WorldPrincipal.World,
                knownSubject: true,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0
            );
            return;
        }

        if (preflight) {
            return;
        }
        _ = body.ApplyTargetedEffect(
            // A world-authored kinematic effect has no affecting body. Passing the recipient here would mint a
            // false Affected fact on its next action pass and could recursively trigger unrelated body actions.
            sourceIndex: -1,
            instruction: new CompiledBodyInstruction(
                Operation: operation.Operation,
                Value: operation.Value,
                Direction: operation.Direction,
                DurationTicks: operation.DurationTicks,
                StateSlot: -1
            )
        );
    }
    private void FireFieldPaint(CompiledWorldEffect effect, string ruleName, ulong tick, bool preflight) {
        if (m_population.Fields is not { } lattice) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.FieldUnavailable, ruleName: ruleName, effect: in effect, tick: tick, detail: "no live field lattice is installed");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }
            return;
        }

        var paint = effect.Paint!.Value;
        if (!lattice.TryFieldIndex(name: paint.Field, field: out _)) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.FieldUnavailable, ruleName: ruleName, effect: in effect, tick: tick, detail: $"live field '{paint.Field}' is unavailable");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }
            return;
        }
        if (preflight) {
            return;
        }
        _ = lattice.PaintSphere(
            fieldName: paint.Field,
            centerX: paint.X,
            centerY: paint.Y,
            centerZ: paint.Z,
            radius: paint.Radius,
            operation: paint.Operation,
            value: paint.Value
        );
    }
    private bool ApplyWorldRuleMutation(in CompiledWorldEffect effect, string ruleName, WorldMutation mutation, ulong tick, int connectionId, long correlationId, bool preMetered, bool preflight) {
        if (!preflight) {
            var applied = TryApplyMutation(
                mutation: mutation,
                tick: tick,
                connectionId: connectionId,
                correlationId: correlationId,
                preMetered: preMetered
            );
            if (!applied) {
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: in effect, tick: tick, detail: "the ordinary mutation door refused the effect; its mutation rejection names the concrete reason");
            }
            return applied;
        }

        var current = m_definition;

        if (!TryCompose(
            current: current,
            mutation: mutation,
            tick: tick,
            instanceIdentity: InstanceIdentity,
            candidate: out var candidate,
            reason: out var composeReason,
            evictedKey: out _,
            patterns: m_patterns
        )) {
            m_ruleStatePreflightRejected = true;
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: in effect, tick: tick, detail: composeReason);
            return false;
        }

        candidate = RebaseCellTraits(candidate: candidate, mutation: mutation, original: current, tick: tick);

        if (!TryValidateMutationCandidate(candidate: candidate, mutation: mutation, reason: out var validationReason)) {
            m_ruleStatePreflightRejected = true;
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: in effect, tick: tick, detail: validationReason);
            return false;
        }

        string? refusal = null;
        if (
            (candidate.Adjacencies is { Count: > 0 }) &&
            AdjacencyProofInputsChanged(candidate: candidate, current: current, mutation: mutation)
        ) {
            refusal = "the mutation changes an adjacency overlap input and requires world.load/world.reload";
        } else if (ExceedsBootDerivedFaceReservation(candidate: candidate, reason: out var reservationReason)) {
            refusal = reservationReason;
        } else if (AffectsRenderEnvelope(mutation: mutation) && !m_envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
            refusal = capacityReason;
        } else if (!m_population.CanInstallFields(definition: candidate, reason: out var fieldReason)) {
            refusal = fieldReason;
        } else if (AffectsSolidField(mutation: mutation) && !TryBuildSolids(definition: candidate, reason: out var solidReason, solids: out _)) {
            refusal = solidReason;
        }
        if (refusal is not null) {
            m_ruleStatePreflightRejected = true;
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: in effect, tick: tick, detail: refusal);
            return false;
        }

        // Make the private candidate visible to the next preflighted effect. Transaction steps cannot add or remove
        // state rows, so their compiled row ordinals remain valid while cell values and keys move.
        m_definition = candidate;

        return true;
    }
    // A contiguous run of state effects is one transaction boundary. Compose and validate the entire ordered run
    // against a private candidate first; only a clean run is replayed through the ordinary apply/journal door. Live
    // fromState reads still see every preceding candidate write because m_definition is temporarily the candidate,
    // but no candidate escapes this synchronous preflight and the installed definition is restored in finally.
    private bool PreflightWorldRuleStateEffects(CompiledWorldEffect[] effects, string ruleName, int start, int end, ulong tick, ulong stepTicks) {
        var installed = m_definition;
        m_ruleStatePreflightRejected = false;

        try {
            for (var index = start; index < end; index++) {
                _ = FireWorldRuleEffect(
                    effect: effects[index],
                    ruleName: ruleName,
                    stepTicks: stepTicks,
                    tick: tick,
                    preflight: true
                );

                if (m_ruleStatePreflightRejected) {
                    return false;
                }
            }

            return true;
        } finally {
            m_definition = installed;
            m_ruleStatePreflightRejected = false;
        }
    }
    // Body state, not document state: the same WorldBody.Pose door ApplyCommand's SnapPose arm (body.pose) uses,
    // but as the world's own act — no drive-gate or grant check, since a gated body is one a rule still needs to
    // move.
    private void FirePoseEffect(CompiledWorldEffect effect, string ruleName, ulong tick, bool preflight) {
        // A '$cell:' indirection yields the cell's integer, which may exceed int — a body index it can never name.
        var spelled = ResolveOperandKey(
            key: effect.Key,
            keyFrom: effect.KeyFrom,
            tick: tick
        );

        if (
            !long.TryParse(
                s: spelled,
                style: System.Globalization.NumberStyles.AllowLeadingSign,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var resolved
            ) ||
            (resolved < 0L) ||
            (resolved > int.MaxValue)
        ) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: in effect, tick: tick, detail: $"key '{spelled}' is not a body index");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }

            return;
        }

        var bodyIndex = ((int)resolved);

        if (Body(index: bodyIndex) is not { } body) {
            ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: in effect, tick: tick, detail: $"body:{bodyIndex} is inactive");
            if (preflight) {
                m_ruleStatePreflightRejected = true;
            }

            return;
        }

        CompiledWorldPose pose;

        if (effect.Pose is { } literal) {
            pose = literal;
        } else if (WorldDefinitionRows.FindSpawnPoint(
            spawnPoints: m_definition.SpawnPoints,
            id: effect.Row
        ) is { } point) {
            var spawn = FixedSpawnPoint.Compile(point: in point);

            pose = new CompiledWorldPose(
                Position: spawn.Position,
                YawRadians: spawn.YawRadians,
                PitchRadians: FixedQ4816.Zero,
                RollRadians: FixedQ4816.Zero
            );
        } else {
            Console.Error.WriteLine(value: $"[world.rule: pose skipped — spawnPoint '{effect.Row}' is no longer declared]");

            return;
        }

        if (preflight) {
            return;
        }
        body.Pose(
            position: pose.Position,
            yawRadians: pose.YawRadians,
            pitchRadians: pose.PitchRadians,
            rollRadians: pose.RollRadians
        );
        Console.Error.WriteLine(value: $"[world.rule: pose body:{bodyIndex} -> ({pose.Position.X}, {pose.Position.Y}, {pose.Position.Z})]");
    }
    // Recompiles the rules section and prunes the edge latch to the surviving names. The compiler is called here
    // UNWRAPPED because WorldDefinitionValidator already compiled this exact candidate and refused it if it could
    // not — the same trusted-second-call shape every other derived-state rebuild in Install has.
    private void RecompileRules(WorldDefinition definition) {
        m_rules = WorldRuleCompiler.CompileAll(definition: definition);
        m_interactions = WorldRuleCompiler.CompileAllInteractions(definition: definition);

        m_ruleGateHeld.Prune(compiled: m_rules);
        m_interactionGateHeld.Prune(compiled: m_interactions);
        ReconcileDecisions();
        ReconcilePatterns(definition);
        m_population.BindFlockAffinities(definition, EvaluateFlockAffinity);
    }
    private bool RuleGateOpen(CompiledWorldPredicate[] gate, ulong tick) {
        if (gate.Length == 0) {
            return true;
        }

        Span<bool> stack = stackalloc bool[WorldRuleCapacity.MaxPredicateTokens];
        var top = 0;

        foreach (var predicate in gate) {
            if (predicate.Kind == CompiledWorldPredicateKind.Not) {
                stack[top - 1] = !stack[top - 1];
                continue;
            }
            if (predicate.Kind is CompiledWorldPredicateKind.All or CompiledWorldPredicateKind.Any) {
                var start = (top - predicate.Arity);
                var result = (predicate.Kind == CompiledWorldPredicateKind.All);

                for (var index = start; index < top; index++) {
                    result = ((predicate.Kind == CompiledWorldPredicateKind.All)
                        ? (result && stack[index])
                        : (result || stack[index]));
                }

                top = start;
                stack[top++] = result;
                continue;
            }

            if (predicate.LeftExpression is { } leftExpression) {
                stack[top++] = TryEvaluateExpression(leftExpression, predicate.ValueKind, tick, out var leftValue) &&
                    TryEvaluateExpression(predicate.RightExpression!, predicate.ValueKind, tick, out var rightValue) &&
                    WorldFactHolds(predicate.Comparison, leftValue, false, rightValue, false);
                continue;
            }
            var value = ReadWorldFact(
                operand: predicate.Left,
                tick: tick
            );

            // The comparand is EITHER the compile-time constant (Comparand null) or a second live operand read on the
            // SAME terms as the primary side — the cross-row spelling of compareState. Both facts are read from THIS
            // tick's live m_definition, so a rule that just advanced its own comparand row (a self-advancing
            // schedule) sees the post-advance value on the VERY NEXT evaluation, never the value it opened against.
            var expected = ((predicate.Comparand is { } comparand)
                ? ReadWorldFact(
                    operand: comparand,
                    tick: tick
                )
                : new WorldFact(
                    Value: predicate.Value,
                    Kind: predicate.ValueKind,
                    IsForever: false
                )
            );

            stack[top++] = WorldFactHolds(
                comparison: predicate.Comparison,
                value: value.Value,
                valueIsForever: value.IsForever,
                expected: expected.Value,
                expectedIsForever: expected.IsForever
            );
        }

        return ((top == 1) && stack[0]);
    }
    private static bool WorldFactHolds(ActionStateComparison comparison, long value, bool valueIsForever, long expected, bool expectedIsForever) {
        var sign = ((valueIsForever, expectedIsForever)) switch {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => value.CompareTo(value: expected),
        };

        return comparison switch {
            ActionStateComparison.Equal => (sign == 0),
            ActionStateComparison.NotEqual => (sign != 0),
            ActionStateComparison.Less => (sign < 0),
            ActionStateComparison.LessOrEqual => (sign <= 0),
            ActionStateComparison.Greater => (sign > 0),
            _ => (sign >= 0),
        };
    }
    // The live half of link liveness: each DIRECT projection in the tick's frozen graph whose delivered snapshot tick
    // advanced is one refresh. An authored row the source could not resolve contributes no projection at all, which
    // is exactly "nothing was delivered" — the staleness count rises and the grace comparison decides. Replay drives
    // this from taped LinkDelivery entries instead; a shadow server holds no adjacency source, so the two never
    // double-count.
    private void ObserveAdjacencyDeliveries() {
        if (m_population.Adjacencies is not { } adjacencies) {
            return;
        }

        var projections = adjacencies.Visuals();

        for (var index = 0; (index < projections.Count); index++) {
            var projection = projections[index];

            if (
                !projection.Direct ||
                !m_events.ObserveLinkDelivery(
                adjacencyName: projection.Name,
                deliveredTick: projection.Neighbour.SnapshotTick
            )
            ) {
                continue;
            }

            LinkDeliveryTap?.Invoke(obj: projection.Name);
        }
    }
    private void StepCore(in FixedStepContext context) {
        // The per-tick mutation-dispatch allowance opens HERE, before either half of the tick that spends it: the
        // addon seam's pre-flight (TickAddons, immediately below) and the drain that applies what it — and every peer
        // submission buffered since the last step — enqueued.
        m_mutationBudget.BeginTick();
        m_addons?.TickAddons(tick: (context.Tick + 1UL));
        _ = DrainPendingOps(tick: context.Tick);
        TransferForwarder?.ResolveContinuations(source: this);
        m_inputHold.PrepareParticipants(population: m_population);

        m_tickWrittenCount = 0;

        while (m_intents.TryDequeue(result: out var submission)) {
            if (Body(index: submission.EntityIndex) is not { } body) {
                continue;
            }

            _ = ApplyIntentSubmission(
                body: body,
                submission: in submission
            );
        }

        ApplyFederatedIntents();

        m_addons?.ApplyContributions(tick: (context.Tick + 1UL));
        FoldChannelContributions();
        m_inputHold.Apply(population: m_population);

        // Settle m_contended for real now that the WHOLE tick's writers have run — the seat drain AND the addon
        // contributions: a queue's dequeue order says nothing about whether a body was genuinely contended for the tick
        // as a whole, only ReportContention's own observation of the FULL set could (see its remarks) — this is that
        // observation, applied once per tracked entity rather than mid-drain.
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            m_contended[m_tickWrittenEntity[index]] = m_tickCollided[index];
        }

        // The context-sensitive-button interception's eligibility pass (the RPG A-button) — resolved against the
        // PRE-MOVE positions (this tick's population has not advanced yet), so a rising edge computed inside
        // AdvanceSeats below diverts into an Engage instead of ever reaching the avatar's action track.
        Span<int> engageProbeOrdinals = stackalloc int[Population.LocalSeatCount];
        Span<int> engageProbeScreens = stackalloc int[Population.LocalSeatCount];
        Span<bool> engageEdges = stackalloc bool[Population.LocalSeatCount];

        ResolveEngageProbes(
            ordinals: engageProbeOrdinals,
            screens: engageProbeScreens
        );

        var tick = (context.Tick + 1UL);

        m_population.Adjacencies?.BeginTick(tick: tick);
        // Immediately after the projection graph freezes, so "did this seam refresh" is read off the SAME pinned
        // image contact and rendering will read for this tick, never a delivery that lands mid-step.
        ObserveAdjacencyDeliveries();

        var stepStartEngineTick = (context.ElapsedTicks - context.StepTicks);

        // Release every carry relationship this tick's drain invalidated (a partner gone inactive, a kit retune away
        // from the facet either side needs) BEFORE the advance passes, so an orphaned target re-enters rigid
        // integration and contact in the same tick its carrier disappeared rather than skipping one.
        m_population.PrepareCarriedBodies();
        // Sample every active body's medium surface BEFORE either half of the tick advances it, so a medium
        // hold's phase-4 law (inside AdvanceSimulated/AdvanceSeats' own body.Advance calls) reads this tick's
        // surface, never last tick's.
        m_population.SampleMediumSurfaces();
        m_population.AdvanceSimulated(
            tick: tick,
            stepTicks: context.StepTicks,
            stepStartEngineTick: stepStartEngineTick
        );
        m_population.AdvanceSeats(
            tick: tick,
            stepTicks: context.StepTicks,
            stepStartEngineTick: stepStartEngineTick,
            engageProbeOrdinals: engageProbeOrdinals,
            engageEdges: engageEdges
        );
        m_population.ResolveDynamicContacts();
        m_population.ResolveTethers();
        m_population.UpdateCarriedBodies();
        m_population.CompleteStep(tick: tick);
        foreach (var designation in m_population.DesignationOutputs) {
            _ = ApplyDesignationCore(
                designation: designation,
                principal: WorldPrincipal.Console,
                knownSubject: true,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0
            );
        }
        m_population.ClearDesignationOutputs();

        // Kit-fired `generate` effects, staged during THIS tick's advance and enqueued through the ORDINARY mutation
        // pipeline for the NEXT tick's drain — the same door a console world.generate and a world rule both use, so
        // one mechanism covers all three rather than three. The one-tick latency is real and reported: this is the
        // first ActionEffect to write the DOCUMENT rather than per-body state, so it is the first to pay the
        // pipeline's own round trip. The acting principal is WorldPrincipal.World whichever body fired it — the
        // effect is the world's authored program acting, not the seat (see that principal's remarks).
        foreach (var invocation in m_population.GeneratorInvocationOutputs) {
            EnqueueMutation(mutation: new WorldMutation.Generate(
                Principal: WorldPrincipal.World,
                Row: invocation.Row
            ));
        }

        m_population.ClearGeneratorInvocationOutputs();

        // Kit-fired `judge` effects, staged during THIS tick's advance — graded and folded into m_judgeGrades right
        // here, within the SAME Step call, rather than through the mutation pipeline: the acting body's own
        // last-grade fact is not a document row, so judge.state observes this tick's grade on this tick's read-back
        // instead of paying Generate's own next-tick round trip. Graded against context.ElapsedTicks, not the
        // simulation-step ordinal `tick` above — RhythmJudge compares against MusicClock.TicksPerBeat, which is
        // engine-tick-denominated (the SAME domain ElapsedTicks carries and MusicClock.Advance just advanced by
        // context.StepTicks), so the step ordinal is the wrong unit to grade against. A judgeRef with no clock to
        // grade against (a world declaring judges with no music row) still records the firing tick against a null
        // (miss) grade.
        foreach (var invocation in m_population.JudgeInvocationOutputs) {
            var windows = FindJudgeWindows(judgeRef: invocation.JudgeRef);
            var grade = (((m_musicClock is { } clock) && (windows is not null))
                ? Puck.Audio.Simulation.RhythmJudge.Evaluate(
                    tick: context.ElapsedTicks,
                    clock: clock,
                    windows: windows
                )
                : (Puck.Audio.Simulation.JudgeWindow?)null);

            m_judgeGrades[(invocation.EntityIndex, invocation.JudgeRef)] = (grade?.Grade, context.ElapsedTicks);
        }

        m_population.ClearJudgeInvocationOutputs();
        if (m_population.DurableStateOutputs.Count > 0) {
            DurableStateOutputTap?.Invoke(obj: m_population.DurableStateOutputs);
            foreach (var output in m_population.DurableStateOutputs) {
                var submission = new WorldDocumentSubmission(
                    SourceDocumentId: (m_definition.DocumentId ?? string.Empty),
                    OwnerDocumentId: output.PlayerId,
                    Tick: output.Tick,
                    Slot: output.Value.Name,
                    Kind: output.Kind,
                    StorageKind: output.StorageKind,
                    Value: ((output.StorageKind == ActionStateKind.Counter)
                    ? output.Value.Value.Value
                    : checked((long)output.Value.TimerTicks))
                );

                m_lastDocumentReceipt = m_profiles.Submit(submission: submission);
                DocumentSubmissionTap?.Invoke(obj: m_lastDocumentReceipt.Value);
            }
        }

        // Route every fired probe into an ordinary Engage, through the SAME authority path a manual body.engage
        // takes — see ResolveEngageProbes for why this is expected to succeed (its own eligibility pass already
        // re-checks CheckEngage), so a denial here can only mean the grant table changed between the two passes on
        // this single-threaded step (an admin revoke applied in between — not a concurrent race, the step runs one
        // thread) — rare enough to accept as a swallowed press rather than a second suppression path.
        for (var slot = 0; (slot < Population.LocalSeatCount); slot++) {
            if (!engageEdges[slot]) {
                continue;
            }

            var principal = WorldPrincipal.Seat(slot: slot);
            var target = GrantSubject.Screen(index: engageProbeScreens[slot]);

            if (m_engagement.Compose(
                actingPrincipal: principal,
                entityIndex: slot,
                exclusive: true,
                target: target,
                targetPrincipal: principal
            )) {
                Console.Error.WriteLine(value: $"[world.engage: {principal.Describe()} auto-engaged {target.Describe()} — context button]");
            }
        }

        // Collect this tick's world-scoped events AFTER the population settles (so positions/occupancy are this
        // tick's) and BEFORE the addon read pump, so ResolveReads can stage them into the SAME batch as this tick's
        // disclosures/answers.
        m_events.Collect(
            definition: m_definition,
            population: m_population
        );

        // The music clock/director step HERE — immediately after Collect() so this tick's own edges (never a stale
        // tick's) drive this tick's transition arming, and before anything else reads m_events.Edges (one call site,
        // one reader, no second-consumer ordering to pin).
        if ((m_musicClock is { } musicClock) && (m_musicDirector is { } musicDirector)) {
            var previousElapsedTicks = musicClock.ElapsedTicks;
            var boundary = musicClock.Advance(stepTicks: context.StepTicks);

            // Diegetic-instrument clock fold — see InstrumentClockBoundary's own remarks for why holding the screen
            // application is the whole gate (never a WorldSessionLever) and why only Beat, never Bar, is contributed.
            boundary |= InstrumentClockBoundary(
                previousElapsedTicks: previousElapsedTicks,
                currentElapsedTicks: musicClock.ElapsedTicks
            );

            musicDirector.Step(
                boundary: boundary,
                edges: MusicDirectorFactory.ProjectSenseEdges(edges: m_events.Edges),
                tick: tick
            );

            // Fire the music.transition cue lane on the SAME tick the transition committed — never a later tick's
            // read-back of LastTransitionTick, which would fire once per subsequent Step call instead of once.
            if (musicDirector.LastTransitionTick == tick) {
                MusicTransitionTap?.Invoke(obj: tick);
            }

            // The active-layer set is level-triggered (never queued), so the tap fires on ANY tick the set differs
            // from what was last tapped — not only a transition-commit tick, and not gated to only fire once.
            if (!ActiveLayerSetsEqual(a: musicDirector.ActiveLayerTuneIds, b: m_lastTappedActiveLayerTuneIds)) {
                m_lastTappedActiveLayerTuneIds = [.. musicDirector.ActiveLayerTuneIds];
                MusicLayerTap?.Invoke(obj: m_lastTappedActiveLayerTuneIds);
            }

            // Fire the music.embellishment cue lane on the SAME tick it fired — the same reasoning as the transition
            // tap immediately above.
            if (musicDirector.LastEmbellishmentTick == tick) {
                MusicEmbellishmentTap?.Invoke(obj: musicDirector.LastEmbellishmentPatchId!);
            }
        }

        // World rules evaluate HERE — after the event feed (so a $region gate reads this tick's settled occupancy)
        // and before the addon read pump and the snapshot (so a rule's write is visible to the same tick's guest
        // reads and delivery).
        EvaluateWorldRules(
            tick: tick,
            stepTicks: context.StepTicks
        );
        StepFields(tick: tick);
        // Escrow recovery evaluates on the SAME terms, right beside rules — see ReclaimExpiredEscrows' own remarks.
        ReclaimExpiredEscrows(tick: tick);
        m_transferEscrow.ReclaimExpired(tick: tick);
        // Contribution tenure recovery — the same shape again, for a presence-tenure slot whose contributor's link
        // went unreachable past its authored grace.
        SweepContributionTenure(tick: tick);
        // Placement response sweep — AFTER StepFields, so a response condition reads this tick's own lattice writes;
        // a state-driven prototype swap for a placement carrying a Respond trait (see WorldPlacementResponse).
        SweepPlacementResponses(tick: tick);
        // Reconnect-park recovery — the same tick-driven, replay-deterministic shape ReclaimExpiredEscrows already
        // establishes, for a disconnected body's deferred teardown instead of an unaccepted ownership offer's. The
        // body half only: a peer generation's grant rows go at its PeerDisconnected event, and a restored parked
        // generation's go at RestoreCheckpoint, so an expiring park holds nothing to release here.
        m_population.ReclaimExpiredParks(tick: tick);
        m_addons?.ResolveReads(tick: (context.Tick + 1UL));
        // Fold this tick's routed intents into their targets BEFORE the snapshot is built.
        m_engagement.FoldTick();

        // Step every booted machine off THIS tick's freshly-folded pads: reads WorldEngagement.BuildPadSnapshot()
        // directly, in-process, no client/wire round-trip. Runs in EVERY boot shape via WorldServerStepShell.Step
        // (headless and windowed alike both call WorldServer.Step) — ROM state IS sim state, not presentation-fed.
        // context.StepTicks is forwarded exactly, preserving the exact-rational T-cycle bridge.
        m_machines.Advance(
            stepTicks: context.StepTicks,
            pads: m_engagement.BuildPadSnapshot()
        );

        // A body-target route's contribution lands on the TARGET's NEXT tick — FoldTick runs after this tick's
        // population has already advanced, so there is no earlier point this tick where the target could still fold
        // it in. Queued through the ordinary intent path (never LoopbackTransport's IntentTap), so it is re-derived at
        // replay time rather than taped directly — see WorldEngagement's class remarks on replay visibility.
        foreach (var contribution in m_engagement.BodyContributions) {
            EnqueueIntent(submission: new IntentSubmission(
                Tick: (context.Tick + 2UL),
                EntityIndex: contribution.TargetBody,
                Intent: contribution.Intent,
                Principal: contribution.Principal
            ));
        }

        EmitSnapshot(
            tick: (context.Tick + 1UL),
            stepTicks: context.StepTicks
        );
        m_lastCompletedTick = (context.Tick + 1UL);
        m_lastStepTicks = context.StepTicks;
        m_lastCompletedEngineTicks = context.ElapsedTicks;
    }

    /// <summary>Applies one submission to a live body under the per-tick Drive check, and returns the verdict that
    /// decided it. The one write path every intent producer shares — the seat drain and every mounted addon's staged
    /// contributions — so authority, the fold routing below, and the denial latch can never diverge between them.
    /// <para>A submission whose principal does not hold <see cref="WorldCapability.Drive"/> over the target body
    /// applies nothing and is reported once per denial episode (a revoked driver keeps submitting; the first
    /// refused tick logs, then the body idles until re-granted). Allocation-free, O(1). The line prints the
    /// verdict's reason, so distinct denial causes such as "exclusively reserved by seat1" and "no grant names it"
    /// surface as distinct messages. The <c>m_driveDenied</c> reporting latch stays deliberately outside the
    /// verdict.</para>
    /// <para>An allowed submission then routes one of two ways, because one body has exactly one base: the
    /// participant that owns it. The body's owning seat or peer (its principal index equals the entity index) — or
    /// any principal when the body is not human-occupied (<see cref="WorldPopulation.IsHumanOccupied"/>: an
    /// unoccupied body is a bot at full authority by construction) — writes through
    /// <see cref="WorldBody.SubmitIntent"/>, which overwrites, and this tick's write is tracked for contention
    /// reporting. Everything else — an addon's contribution, or a different seat co-driving a body it does not own
    /// — is staged into the per-tick contribution set instead (<see cref="StageContribution"/>, which carries both
    /// the submission's intent and its held-channel composition image) and folded later by
    /// <see cref="FoldChannelContributions"/>; it is never tracked as contention, because a consented (or
    /// default-denied) contribution is a deliberate composition path, not a race.</para></summary>
    /// <param name="body">The live body the submission targets — the caller resolves it, because a submission
    /// naming an entity that holds no body is not an authority outcome and must not be answered as one.</param>
    /// <param name="submission">The tick, entity index, principal, intent, and held-lane image.</param>
    /// <returns>The verdict that decided the check; nothing was applied unless it allows.</returns>
    /// <remarks>A body carrying a nonzero cell on a <see cref="WorldStateRow.GatesDrive"/> row
    /// (<see cref="TryDriveGateVerdict"/>, resynced from live document state — see
    /// <see cref="WorldGrants.SyncState"/>) has its intent refused before the grant table is checked, regardless of
    /// any Drive hold, including an exclusive reservation: a status effect is a fact about the body, not about who
    /// is allowed to drive it, so it outranks a principal that genuinely holds Drive. No rule or effect touches the
    /// grant table to express this; the check reads the state fact directly, the same "deciding fact beyond the
    /// static grant table" shape <see cref="GrantRule.OwnershipHold"/> reads a different fact through. The gate is
    /// released, never latched: once the gate row's cell reads zero, this check passes straight through to the
    /// ordinary <see cref="WorldGrants.Allows"/> call below. <see cref="ApplyCommand"/>'s generic Drive gate checks
    /// the same <see cref="TryDriveGateVerdict"/> before its own <see cref="WorldGrants.Allows"/> call, so a
    /// scripted tape segment (<c>body.fly</c>/<c>EnqueueSegment</c>) is refused by the same fact a raw per-tick
    /// channel submission is.</remarks>
    public GrantVerdict ApplyIntentSubmission(WorldBody body, in IntentSubmission submission) {
        var gated = TryDriveGateVerdict(
            bodyIndex: submission.EntityIndex,
            verdict: out var gatedVerdict
        );
        var verdict = (gated
            ? gatedVerdict
            : m_grants.Allows(
                principal: submission.Principal,
                capability: WorldCapability.Drive,
                subject: GrantSubject.Body(index: submission.EntityIndex)
            )
        );

        if (!verdict.IsAllowed) {
            if (!m_driveDenied[submission.EntityIndex]) {
                Console.Error.WriteLine(value: $"[world.grant denied: {verdict.DescribeRefusal(
                    actor: submission.Principal,
                    dropped: "intent dropped, body idle",
                    subject: $"body:{submission.EntityIndex}",
                    verb: "drive"
                )}]");
                m_driveDenied[submission.EntityIndex] = true;
            }

            return verdict;
        }

        m_driveDenied[submission.EntityIndex] = false;
        m_inputHold.ObserveMeasurement(submission: in submission);

        var bodyIndex = submission.EntityIndex;
        var isOwningParticipant = (((submission.Principal.Kind == PrincipalKind.Seat) || (submission.Principal.Kind == PrincipalKind.Peer)) && (submission.Principal.Index == bodyIndex));
        var isOwningSeat = ((submission.Principal.Kind == PrincipalKind.Seat) && (submission.Principal.Index == bodyIndex));
        var occupied = m_population.IsHumanOccupied(bodyIndex: bodyIndex);

        if (
            isOwningParticipant ||
            !occupied
        ) {
            ReportContention(
                entityIndex: bodyIndex,
                principal: submission.Principal
            );
            body.SubmitIntent(intent: submission.Intent);
            body.SetHeldChannels(channels: submission.HeldChannels);

            if (isOwningSeat) {
                // This tick's `h` and its held-device image — never the ladder's winner (a tape still outranks the
                // former; see WorldBody.NextIntent). Recorded even when nothing ends up contributing this tick, so
                // FoldChannelContributions's common-case check (m_hasContribution) stays the only extra cost an
                // uncontended body pays. The held image is recorded because the fold may have to REPLACE the direct
                // write above with the max of it and a contributor's own composition act.
                m_ownerBase[bodyIndex] = submission.Intent;
                m_ownerHeld[bodyIndex] = submission.HeldChannels;
                m_hasOwnerBase[bodyIndex] = true;
                // The read-back's baseline for THIS write: no pool, no contributors — true unless
                // FoldChannelContributions (below, later this same tick) overwrites it with a real fold, because a
                // contribution actually landed. Reset rather than left stale, so a body contended two ticks ago and
                // quiet since does not keep reporting a pool that no longer exists.
                RecordDirectChannelRead(
                    seat: bodyIndex,
                    intent: submission.Intent
                );
            }
        } else {
            StageContribution(
                bodyIndex: bodyIndex,
                principal: submission.Principal,
                submission: in submission
            );
        }

        return verdict;
    }
    /// <summary>Re-applies a server-authored event through the population and grant doors. Replay calls this same
    /// method; there is no state-install bypass.</summary>
    /// <param name="serverEvent">The ordered event.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverEvent"/> is <see langword="null"/>.</exception>
    public void ApplyServerEvent(WorldServerEvent serverEvent) {
        ArgumentNullException.ThrowIfNull(argument: serverEvent);

        switch (serverEvent) {
            case WorldServerEvent.PeerAdmitted admitted:
                foreach (var peer in admitted.Entries) {
                    m_population.ApplyPeerAdmitted(
                        grantTemplates: [],
                        peer: in peer
                    );

                    foreach (var stale in m_grants.StalePeerGenerations(
                        index: peer.BodyIndex,
                        currentGeneration: peer.Generation
                    )) {
                        foreach (var row in m_grants.Rows(principal: stale)) {
                            Revoke(
                                grant: row,
                                actor: WorldPrincipal.Console
                            );
                        }
                    }
                }

                var installedGrants = new List<WorldGrant>();

                foreach (var grant in admitted.MintedGrants) {
                    if (TryApplyGrant(
                        grant: grant,
                        actor: WorldPrincipal.Console
                    )) {
                        installedGrants.Add(item: grant);
                    }
                }

                foreach (var peer in admitted.Entries) {
                    var installedTemplates = AdmissionTemplatesFor(
                        mintedGrants: installedGrants,
                        peer: peer
                    );

                    m_population.SetPeerAdmissionInstalledGrantTemplates(
                        bodyIndex: peer.BodyIndex,
                        grantTemplates: installedTemplates
                    );
                }

                break;
            case WorldServerEvent.PeerDisconnected disconnected:
                // The body parks with grace; the authority does not. Park serves body continuity (pose, durable
                // state, collidability, targetability) and ApplyPeerDisconnected still defers that half to
                // ReclaimExpiredParks. Authority follows the CONNECTION: while disconnected, nothing can exercise
                // the generation's rows, yet an Exclusive subject it reserved would refuse every live acquirer —
                // for the whole grace window, and forever at rate 0, where the compiled grace is Never and no sweep
                // ever runs. Release is therefore unconditional here, the same path a non-parking disconnect (an
                // authored-zero grace, or no live match) takes; a verified-identity reconnect that resumes the
                // parked BODY re-mints its admission templates through the ordinary PeerAdmitted event
                // (WorldServer.TryAdmitVerifiedParticipant's resume arm), so only live acquisitions beyond the
                // templates fail to survive the gap. It rides this event, so replay re-drives the identical
                // revocations through the identical door at the identical tick, with no separate tape entry.
                foreach (var peer in disconnected.Entries) {
                    m_population.ApplyPeerDisconnected(
                        peer: in peer,
                        tick: NextInputTick
                    );
                }

                foreach (var grant in disconnected.RevokedGrants) {
                    Revoke(
                        grant: grant,
                        actor: WorldPrincipal.Console
                    );
                }

                break;
            default:
                Console.Error.WriteLine(value: $"[world.server-event refused: {serverEvent.GetType().Name} is not declared]");
                return;
        }

        ServerEventTap?.Invoke(obj: serverEvent);
    }
    /// <summary>The administrative drain — applies every buffered document-level operation (mutations, rebuilds,
    /// undo, addon lifecycle changes) without advancing simulation time: no addon tick, no intent drain, no body
    /// integration, no rules, no event collection, and no snapshot delivery. <see cref="DrainPendingOps"/> is
    /// normally reached only from inside <see cref="Step"/>, so an instance that never steps (an authored
    /// <c>simulation.rateHz</c> of 0, or a live <c>world.rate pause</c>) could otherwise never apply the very
    /// mutation that would change that — a permanent self-lock. Called on the host's own master timeline in place
    /// of <see cref="Step"/> for a tick a stopped/paused instance does not take (see <c>Puck.World.WorldInstanceHost</c>'s
    /// per-instance scheduling remarks for the host-side half of this contract — that type lives a layer above this
    /// assembly, hence prose rather than a cref here).</summary>
    /// <remarks>Opens a fresh per-tick mutation-dispatch allowance exactly as <see cref="Step"/>'s own top does
    /// (<see cref="WorldMutationBudgetMeter.BeginTick"/> is a plain clear — safe to call once per administrative
    /// drain, same as once per real tick), so an untrusted principal keeps a steady dispatch rate while stopped
    /// rather than being starved by a budget that never resets. Every applied entry journals against
    /// <see cref="m_lastCompletedTick"/> — the tick that does not move while stopped — so <c>world.undo</c> stays
    /// coherent: an administrative entry undoes exactly like an ordinary one, it is simply attributed to a tick
    /// number that repeats until the instance actually steps again. Document mutations are outside the replay
    /// tape's own recorded scope already (<see cref="Puck.World.WorldReplayTape"/>'s honest-scope remarks — the
    /// tape records the human/authority command stream, never a raw <see cref="Protocol.WorldMutation"/>), so this
    /// method introduces no new tape interaction.</remarks>
    /// <returns><see langword="true"/> when anything applied (a definition delivery occurred).</returns>
    public bool DrainAdministrative() {
        m_mutationBudget.BeginTick();

        return DrainPendingOps(tick: m_lastCompletedTick);
    }
    /// <summary>Buffers one entity's submitted intent for the next <see cref="Step"/>.</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    public void EnqueueIntent(in IntentSubmission submission) {
        m_intents.Enqueue(item: submission);
    }
    /// <summary>Advances the authoritative world by one exact host tick: run every mounted addon's guest code first (see
    /// <see cref="IWorldAddonHost.TickAddons"/>, which applies nothing) → drain the buffered live edits (mutations,
    /// swaps, undo), applying each at the tick boundary and delivering the new definition once if any applied → drain
    /// the tick's submitted intents → apply the addons' staged contributions
    /// (<see cref="IWorldAddonHost.ApplyContributions"/>) → fold every human-occupied body's tick (see
    /// <see cref="FoldChannelContributions"/>) → settle per-body contention over the tick as a whole → advance every
    /// body (peers, then seats) → resolve the addons' reads against the stepped state
    /// (<see cref="IWorldAddonHost.ResolveReads"/>) → deliver the tick's <see cref="WorldSnapshot"/>.</summary>
    /// <remarks>The three addon points are pinned, and each is pinned for a reason: guests run before anything is
    /// applied so a guest's own effect never depends on where in the tick it happened to be pumped; reads resolve
    /// after the step of the tick they were written in, so a verdict, a minted handle, and a pose all describe the
    /// same settled instant. <b>An addon's contribution to a human-occupied body is never a plain overwrite of the
    /// seat's own submission (<see cref="FixedContributionFold"/>).</b> <see cref="ApplyIntentSubmission"/> routes a
    /// non-owning contributor into a per-tick contribution set instead of calling <see cref="WorldBody.SubmitIntent"/>
    /// directly, and <see cref="FoldChannelContributions"/> — the fourth point, run once contributions have finished
    /// landing and before the population advances — folds each occupied body's owning-seat base with its tick's
    /// pooled/unpooled contributions into the single value <see cref="WorldBody.SubmitIntent"/> receives. An
    /// unoccupied body (no seat, or an inactive one) is untouched by any of this and keeps plain overwrite
    /// semantics, because occupancy is what makes a pool exist at all (a bot at full authority is not an oversight
    /// there). <see cref="WorldBody.NextIntent"/>'s tape-outranks-submitted ladder is itself untouched; only how the
    /// submitted tier is produced differs by occupancy.</remarks>
    /// <param name="context">Explicit simulation coordinates for this tick. Hosts and ordinary replay drivers
    /// use <see cref="Advance"/> to derive these from the authority's own checkpointed clock.</param>
    public void Step(in FixedStepContext context) {
        lock (m_authorityGate) {
            StepCore(context: in context);
        }
    }
    /// <summary>Advances one step from this authority's own checkpointed clock. Hosts and replay drivers use this
    /// entry point so restoring a timeline cannot inherit the host pacing counter's old tick or elapsed time.</summary>
    /// <param name="stepTicks">The exact duration of this step in engine ticks.</param>
    /// <exception cref="OverflowException">The completed tick or engine-time coordinate would overflow.</exception>
    public void Advance(ulong stepTicks) {
        lock (m_authorityGate) {
            _ = checked(m_lastCompletedTick + 1UL);
            var context = new FixedStepContext(
                ElapsedTicks: checked(m_lastCompletedEngineTicks + stepTicks),
                StepTicks: stepTicks,
                Tick: m_lastCompletedTick);
            StepCore(in context);
        }
    }
    /// <summary>Submits one envelope into the ordered domain — the single front door every non-intent submission kind
    /// drains through (see <see cref="IWorldServerHost.Submit"/>'s own remarks). Enqueues, then immediately drains
    /// the whole queue inline, so a submission applies synchronously before this call returns — exactly matching the
    /// per-kind synchronous methods it replaces. The in-process <c>LoopbackTransport</c> submits on connection 0;
    /// <c>WorldPeerHost</c> submits each admitted socket peer under its own per-connection id.</summary>
    /// <param name="envelope">The envelope to submit.</param>
    /// <param name="completion">Invoked once with the envelope's typed result, or <see langword="null"/>.</param>
    public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) =>
        EnqueueOrdered(entry: new OrderedEntry.Submission(
            Completion: completion,
            Envelope: envelope
        ));

    // One buffered live-edit op, drained FIFO at the step boundary before intents. Each retains the submitting
    // envelope's connection/correlation identity (see EnqueueMutation's own remarks) so its eventual WorldEditEcho —
    // fired later, from inside DrainPendingOps, not at submit time — still names the right submitter.
    private abstract record PendingOp {
        // SourceAddonInstanceId/ActOrdinal are the addon mutation seam's completion fields: -1/0 for every non-addon
        // submitter (a console/client mutation has no act to complete). A Mutate op WITH a source addon carries them
        // through DrainPendingOps -> WorldAddonRuntime.CompleteMutation so the reserved Answer cell EmitDisclosures
        // already withheld space for gets its verdict staged at ResolveReads(T), for delivery in the guest's batch
        // T+1 — never applied here, only routed. SourceAddonInstanceId names the mounted instance's own stable
        // token (WorldAddonRuntime.MountedAddon.InstanceId), never a positional index — a queued removal or reorder
        // draining ahead of this op must not deliver its completion to whatever guest now sits where the source
        // guest used to. OutcomeObserved is the tape's own completion field (see EnqueueMutation's own remarks):
        // non-null only for the one dispatch point (ApplyEnvelope) MutationTap already covers, invoked exactly
        // once, right after this op's own TryApplyMutation outcome is known.
        public sealed record Mutate(WorldMutation Mutation, int ConnectionId, long CorrelationId, long SourceAddonInstanceId = -1L, ushort ActOrdinal = 0, Action<bool>? OutcomeObserved = null) : PendingOp;
        public sealed record Rebuild(WorldRebuildRequest Request, WorldPrincipal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash = null, string? PreparationFailure = null) : PendingOp;
        public sealed record Undo(int Count, WorldPrincipal Principal, int ConnectionId, long CorrelationId) : PendingOp;
    }
    // The evaluation binding a latch entry belongs to: a forEach key or a region-interaction carrier in Left with
    // Right -1, a distance-interaction pair in both, and None for a rule evaluated once.
    private readonly record struct LatchKey(int Left, int Right) {
        public static readonly LatchKey None = new(
            Left: -1,
            Right: -1
        );

        // Checkpoint spelling: "" for None, else ":left" or ":left:right" — ':' is reserved out of WorldCellName, so
        // the rule name it trails can never contain it. KEEP IN SYNC with TryParse.
        public string Format() => ((Left < 0)
            ? string.Empty
            : ((Right < 0)
                ? string.Create(
                    provider: System.Globalization.CultureInfo.InvariantCulture,
                    handler: $":{Left}"
                )
                : string.Create(
                    provider: System.Globalization.CultureInfo.InvariantCulture,
                    handler: $":{Left}:{Right}"
                )
            )
        );
        public static bool TryParse(ReadOnlySpan<char> text, out LatchKey binding) {
            binding = None;

            if (text.IsEmpty) {
                return true;
            }

            if (text[0] != ':') {
                return false;
            }

            text = text[1..];

            var split = text.IndexOf(value: ':');
            var leftText = ((split < 0)
                ? text
                : text[..split]
            );
            var rightText = ((split < 0)
                ? ReadOnlySpan<char>.Empty
                : text[(split + 1)..]
            );

            if (!int.TryParse(
                s: leftText,
                style: System.Globalization.NumberStyles.None,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var left
            )) {
                return false;
            }

            var right = -1;

            if (
                (split >= 0) &&
                !int.TryParse(
                s: rightText,
                style: System.Globalization.NumberStyles.None,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out right
            )
            ) {
                return false;
            }

            binding = new LatchKey(
                Left: left,
                Right: right
            );

            return true;
        }
    }
    // One family's edge latch (rules or interactions), per rule name and per binding, kept outside the compiled
    // array because a rule's own effect recompiles it. A bound entry not touched between BeginSweep and EndSweep is
    // closed: that is how a pair that left range, or a carrier that despawned, re-arms and is forgotten.
    private sealed class RuleLatch {
        private readonly Dictionary<string, Dictionary<LatchKey, bool>> m_byRule = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<LatchKey> m_touched = [];
        private readonly List<KeyValuePair<LatchKey, bool>> m_hashScratch = [];

        public int Count {
            get {
                var count = 0;

                foreach (var bindings in m_byRule.Values) {
                    count += bindings.Count;
                }

                return count;
            }
        }

        public void BeginSweep() => m_touched.Clear();
        public void AppendStateHash(ref Fnv1aHash hash, CompiledWorldRule[] compiled) {
            hash.Add(value: ((uint)compiled.Length));

            foreach (var rule in compiled) {
                hash.Add(value: Fnv1aHash.Compute(values: rule.Name.AsSpan()));

                if (!m_byRule.TryGetValue(
                    key: rule.Name,
                    value: out var bindings
                )) {
                    hash.Add(value: 0U);
                    continue;
                }

                m_hashScratch.Clear();
                foreach (var pair in bindings) { m_hashScratch.Add(pair); }
                var ordered = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(m_hashScratch);
                ordered.Sort(
                    comparison: static (left, right) => {
                        var result = left.Key.Left.CompareTo(value: right.Key.Left);

                        return ((result != 0)
                            ? result
                            : left.Key.Right.CompareTo(value: right.Key.Right)
                        );
                    }
                );
                hash.Add(value: ((uint)ordered.Length));

                foreach (var (binding, held) in ordered) {
                    hash.Add(value: ((uint)binding.Left));
                    hash.Add(value: ((uint)binding.Right));
                    hash.Add(value: ((byte)(held ? 1 : 0)));
                }
            }
        }
        public Dictionary<LatchKey, bool> Bindings(string name) {
            ref var bindings = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                dictionary: m_byRule,
                exists: out _,
                key: name
            );

            return (bindings ??= []);
        }
        public void Clear() => m_byRule.Clear();
        public void EndSweep(Dictionary<LatchKey, bool> bindings) {
            // Dictionary.Remove does not invalidate an in-flight enumerator.
            foreach (var pair in bindings) {
                if (!m_touched.Contains(item: pair.Key)) {
                    _ = bindings.Remove(key: pair.Key);
                }
            }
        }
        public void Flatten(List<(string, bool)> into) {
            foreach (var (name, bindings) in m_byRule) {
                foreach (var (binding, held) in bindings) {
                    into.Add(item: (string.Concat(
                        str0: name,
                        str1: binding.Format()
                    ), held));
                }
            }
        }
        // Held when the gate held at the last evaluation of any binding of the rule.
        public bool Held(string name) {
            if (!m_byRule.TryGetValue(
                key: name,
                value: out var bindings
            )) {
                return false;
            }

            foreach (var held in bindings.Values) {
                if (held) {
                    return true;
                }
            }

            return false;
        }
        // Every surviving name keeps its entries; a name no longer compiled loses them.
        public void Prune(CompiledWorldRule[] compiled) {
            if (m_byRule.Count == 0) {
                return;
            }

            var live = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var rule in compiled) {
                if (rule.Decision is null) { _ = live.Add(item: rule.Name); }
            }

            foreach (var name in m_byRule.Keys) {
                if (!live.Contains(item: name)) {
                    _ = m_byRule.Remove(key: name);
                }
            }
        }
        // The inverse of Flatten; a checkpoint entry that does not parse is dropped rather than mis-keyed.
        public void Restore(string key, bool held) {
            var split = key.IndexOf(value: ':');
            var name = ((split < 0)
                ? key
                : key[..split]
            );

            if (LatchKey.TryParse(
                binding: out var binding,
                text: ((split < 0)
                    ? ReadOnlySpan<char>.Empty
                    : key.AsSpan(start: split))
            )) {
                Bindings(name: name)[binding] = held;
            }
        }
        public void Touch(LatchKey binding) => m_touched.Add(item: binding);
    }
    // One entry in the ordered domain (see m_ordered's own remarks): the envelope plus the completion its submitter
    // supplied (null when the caller does not need one).
    private abstract record OrderedEntry {
        public sealed record Submission(SubmissionEnvelope Envelope, Action<WorldSubmissionResult>? Completion) : OrderedEntry;
        public sealed record ServerEvent(WorldServerEvent Value) : OrderedEntry;
    }
}
