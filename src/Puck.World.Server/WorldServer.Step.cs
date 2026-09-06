using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Observes a music segment transition the instant it commits (the same tick <c>MusicDirector</c>
    /// records it, from the music-step call site in <see cref="StepCore"/>) — mirroring
    /// <see cref="SaveEffectTap"/>/<see cref="IWorldMachineHost.MachineLifecycleTap"/>'s "the server calls out, the
    /// composition root supplies the capability" shape: this project references no audio director, so it cannot fire
    /// the <c>music.transition</c> cue itself. Carries nothing but the tick — the committed segment ids are already
    /// re-derivable from <c>MusicDirector.LastTransitionFromSegmentId</c>/<c>LastTransitionToSegmentId</c>, so no
    /// second value need round-trip through the tap. A <see langword="null"/> tap is a silent no-op, the same
    /// convention every other tap here follows; every live boot shape wires one (<c>WorldPostBuildWiring.Install</c>).
    /// Never taped — see <c>MusicReplayReDerivabilityLawTests</c>: the director's own state is purely
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
    private readonly List<Puck.Audio.Simulation.MusicSenseEdge> m_senseEdgeScratch = [];
    private IReadOnlyList<Puck.Audio.Simulation.MusicSenseEdge> ProjectSenseEdges() {
        MusicDirectorFactory.ProjectSenseEdges(edges: m_events.Edges, projected: m_senseEdgeScratch);

        return m_senseEdgeScratch;
    }
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
            DeliverPending();
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

                m_evaluator.BoundLeft = left;
                m_evaluator.BoundRight = -1;
                applied |= m_evaluator.EvaluateOnce(rule: rule, latch: latch, bindings: bindings, binding: new LatchKey(Left: left, Right: -1), tick: tick, stepTicks: stepTicks);
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

            var budget = interaction.Neighbours;

            foreach (var left in lefts) {
                var kept = 0;

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

                    if (budget > 0) {
                        // Keep the nearest `budget` rights, ascending by distance then index; the sweep evaluates them
                        // after the scan so the kept set is the same whatever order the carriers were listed in.
                        var slot = kept;
                        while ((slot > 0) && ((m_neighbourDistance[slot - 1] > distanceSquared) || ((m_neighbourDistance[slot - 1] == distanceSquared) && (m_neighbourIndex[slot - 1] > right)))) {
                            if (slot < budget) {
                                m_neighbourDistance[slot] = m_neighbourDistance[slot - 1];
                                m_neighbourIndex[slot] = m_neighbourIndex[slot - 1];
                            }
                            slot--;
                        }
                        if (slot < budget) {
                            m_neighbourDistance[slot] = distanceSquared;
                            m_neighbourIndex[slot] = right;
                            kept = Math.Min(val1: (kept + 1), val2: budget);
                        }

                        continue;
                    }

                    m_evaluator.BoundLeft = left;
                    m_evaluator.BoundRight = right;
                    applied |= m_evaluator.EvaluateOnce(rule: rule, latch: latch, bindings: bindings, binding: new LatchKey(Left: left, Right: right), tick: tick, stepTicks: stepTicks);
                }

                for (var index = 0; index < kept; index++) {
                    var right = m_neighbourIndex[index];

                    m_evaluator.BoundLeft = left;
                    m_evaluator.BoundRight = right;
                    applied |= m_evaluator.EvaluateOnce(rule: rule, latch: latch, bindings: bindings, binding: new LatchKey(Left: left, Right: right), tick: tick, stepTicks: stepTicks);
                }
            }
        }

        m_evaluator.BoundLeft = -1;
        m_evaluator.BoundRight = -1;
        latch.EndSweep(bindings: bindings);

        return applied;
    }
    // Resolves a document row once through the catalog's name -> handle dictionary (StateCatalog.TryResolve) and the
    // handle's own LaneOrdinal as a direct row-array index, instead of WorldDefinitionRows.FindStateRow's linear scan
    // over every declared row. Shared by every tick-path reader that starts from a row name (Carriers/CarrierKeys
    // below, WorldServer.BoardEnforcement.cs) — the handle is handed back too, so a caller that walks the row's own
    // cells afterward reads each one through it rather than resolving the row by name again per cell.
    private bool TryResolveDocumentRow(string name, out StateHandle handle, out WorldStateRow? row) {
        var catalog = m_definition.StateCatalog;

        if (
            catalog.TryResolve(lane: StateLane.Document, name: name, handle: out handle) &&
            catalog.TryGetDescriptor(handle: handle, descriptor: out var descriptor) &&
            (((uint)descriptor.LaneOrdinal) < ((uint)m_definition.State.Count))
        ) {
            row = m_definition.State[descriptor.LaneOrdinal];

            return true;
        }

        row = null;

        return false;
    }
    private bool TryResolveCarrierRow(string row, out StateHandle handle, out IReadOnlyList<StateCell>? cells) {
        if (TryResolveDocumentRow(name: row, handle: out handle, row: out var resolved)) {
            cells = resolved!.Cells;

            return (cells is not null);
        }

        cells = null;

        return false;
    }
    // The integer keys a keyed row holds at this moment, ascending — the iteration set of a decision rule. Fills
    // the caller's scratch list; the cells themselves are not retained.
    private void CarrierKeys(string row, List<int> into) {
        into.Clear();

        if (TryResolveCarrierRow(row: row, handle: out _, cells: out var cells)) {
            for (var index = 0; index < cells!.Count; index++) {
                var cell = cells[index];
                if (StateReader.TryParseCandidateIndex(
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
    // plain cell's stored value is its live value; only an advancing cell goes through the reader's as-of-tick walk,
    // through the row's own handle rather than a per-cell ReadStateCell(string) name resolve.
    private void Carriers(string row, ulong tick, List<int> into) {
        into.Clear();

        if (!TryResolveCarrierRow(row: row, handle: out var handle, cells: out var cells)) {
            return;
        }

        for (var cellIndex = 0; cellIndex < cells!.Count; cellIndex++) {
            var cell = cells[cellIndex];
            if (
                !StateReader.TryParseCandidateIndex(
                index: out var index,
                key: cell.Key
            ) ||
                (Body(index: index) is null)
            ) {
                continue;
            }

            var nonzero = (((cell.Advance is null) && (cell.Cycle is null))
                ? (cell.Value != 0L)
                : (ReadStateCellByHandle(
                    handle: handle,
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
    // The rule/interaction ARRAY is snapshotted by the evaluator's loop (the m_rules/m_interactions read below), which
    // is a different thing from the state the gates read: a rule's own effect installs a new definition, which
    // reassigns m_rules/m_interactions. Every row declared at the top of the tick evaluates during this tick; a row
    // ADDED by this tick's effects starts on the next one, the same next-tick boundary every other mutation lands on.
    //
    // Effects apply IMMEDIATELY, not at a boundary: the evaluator's state effects land in TryApplyRuleMutation, which
    // calls TryApplyMutation and installs the composed definition on the spot. So a later rule's gate DOES read an earlier rule's same-tick write — and so
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
        LoadRuleFrame();
        var applied = m_evaluator.Evaluate(rules: m_rules, latch: m_ruleGateHeld, tick: tick, stepTicks: stepTicks);

        applied |= m_evaluator.Evaluate(rules: m_interactions, latch: m_interactionGateHeld, tick: tick, stepTicks: stepTicks);

        FoldRuleFrameMutations(tick: tick);
        m_ruleFrameActive = false;

        if (applied) {
            DeliverPending();
        }
    }
    // The effect arms only the world can fire, on the evaluator's terms. A non-mutating arm (cue, body, field, save,
    // pose) acts, or refuses by name. A document-row arm (HUD panel, placement) submits its own ORDINARY mutation
    // through the ordinary pipeline (admission → compose → whole-document validate → install → journal → echo),
    // stamped WorldPrincipal.World — the SAME door UpsertHudPanel/RemoveHudPanel/UpsertPlacement/RemovePlacement
    // already have from the console or an addon; nothing here is a new admission path. A HUD/placement upsert or
    // remove is never a no-op, so both are submitted — the one exception being a removePlacement on a possessed
    // carrier, which the CarrierPossessed guard refuses outright rather than submitting. SAVE submits no
    // WorldMutation at all (see WorldEffect.Save's remarks).
    EffectOutcome IRuleHost.FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) {
        switch (effect) {
            case EmitCueEffect cue:
                if (!preflight) {
                    FireGameplayCue(effect: cue, tick: tick);
                }
                return EffectOutcome.Skipped;
            case BodyEffect body:
                return (FireBodyEffect(effect: body, ruleName: ruleName, tick: tick, preflight: preflight) ? EffectOutcome.Refused : EffectOutcome.Skipped);
            case PaintFieldEffect paint:
                return (FireFieldPaint(effect: paint, ruleName: ruleName, tick: tick, preflight: preflight) ? EffectOutcome.Refused : EffectOutcome.Skipped);
            case SaveEffect:
                if (preflight) {
                    m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.SaveUnavailable, ruleName: ruleName, effect: effect, tick: tick, detail: "save effects are not atomic transaction steps");
                    return EffectOutcome.Refused;
                }
                if (SaveEffectTap is { } save) {
                    save(tick);
                } else {
                    m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.SaveUnavailable, ruleName: ruleName, effect: effect, tick: tick, detail: "no save-effect host is attached");
                }
                return EffectOutcome.Skipped;
            case PoseEffect pose:
                return (FirePoseEffect(effect: pose, ruleName: ruleName, tick: tick, preflight: preflight) ? EffectOutcome.Refused : EffectOutcome.Skipped);
        }

        // DESPAWN-OF-OWNED-CARRIER GUARD (WorldRuleEffectRefusal.CarrierPossessed): a removePlacement targeting a
        // placement whose Inhabit facet is currently bound to a POSSESSED body (a concrete drive grant — see
        // WorldGrants.IsBodyPossessed's own remarks) is refused rather than fired. A placement's Inhabit/Region facets
        // make an ordinary whole-row upsert/remove a BODY/REGION carrier spawn/despawn (WorldPopulation
        // .ReconcileInhabitants reconciles from ANY accepted mutation, principal-agnostic) — this is the one case
        // that must NOT go through silently, because it would destroy an explicit possession grant's binding out
        // from under it (the slot a later, unrelated inhabitant can then claim). REFUSE, never orphan-to-escrow (see
        // the refusal's own remarks for why).
        if (
            (effect is RemovePlacementEffect removePlacement) &&
            TryFindPossessedInhabitant(placementId: removePlacement.Row, bodyIndex: out var possessedBody, holder: out var possessor)
        ) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.CarrierPossessed, ruleName: ruleName, effect: effect, tick: tick, detail: $"placement '{removePlacement.Row}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}");

            return EffectOutcome.Refused;
        }

        WorldMutation mutation = effect switch {
            UpsertHudPanelEffect upsertHudPanel => new WorldMutation.UpsertHudPanel(Principal: WorldPrincipal.World, Panel: upsertHudPanel.HudPanel),
            RemoveHudPanelEffect removeHudPanel => new WorldMutation.RemoveHudPanel(Principal: WorldPrincipal.World, Id: removeHudPanel.Row),
            UpsertPlacementEffect upsertPlacement => new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.World, Placement: upsertPlacement.Placement),
            RemovePlacementEffect removePlacementFire => new WorldMutation.RemovePlacement(Principal: WorldPrincipal.World, Id: removePlacementFire.Row),
            _ => throw new InvalidOperationException(message: $"world rule effect '{effect.Describe}' has no fire mapping."),
        };

        return (TryApplyRuleMutation(effect: effect, ruleName: ruleName, mutation: mutation, tick: tick, preflight: preflight) ? EffectOutcome.Applied : EffectOutcome.Refused);
    }
    private void FireGameplayCue(EmitCueEffect effect, ulong tick) {
        var cueEffect = effect;
        int? body = null;
        if (cueEffect.Key.Length > 0) {
            var key = ResolveOperandKey(key: cueEffect.Key, keyFrom: cueEffect.KeyFrom, tick: tick);
            if (int.TryParse(s: key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var parsed) && (Body(index: parsed) is not null)) {
                body = parsed;
            }
        }

        var cue = new WorldGameplayCue(Name: cueEffect.Cue, Payload: cueEffect.Payload, Body: body, Tick: tick);
        GameplayCueTap?.Invoke(obj: cue);
        if (m_output.HasNarrationSink) {
            m_output.Narrate(channel: "world.cue", text: $"[world.cue: {cue.Name} tick={tick}{(body is { } index ? $" body:{index}" : string.Empty)}]");
        }
    }
    private bool FireBodyEffect(BodyEffect effect, string ruleName, ulong tick, bool preflight) {
        var bodyEffect = effect;
        var key = ResolveOperandKey(key: bodyEffect.Key, keyFrom: bodyEffect.KeyFrom, tick: tick);
        if (!int.TryParse(s: key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var bodyIndex) || (Body(index: bodyIndex) is not { } body)) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"body '{key}' is inactive");
            return true;
        }

        var operation = bodyEffect.Body;
        if (operation.Operation == BodyMotionOp.Designate) {
            if (!m_population.TryResolveTargetRegister(name: operation.Register!, index: out var registerIndex)) {
                m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: effect, tick: tick, detail: $"target register '{operation.Register}' is unavailable");
                return true;
            }
            if (operation.Designation == WorldBodyDesignationKind.Clear) {
                if (!preflight) {
                    m_population.SetDesignation(bodyIndex: bodyIndex, registerIndex: registerIndex, target: WorldTargetDesignation.None);
                }
                return false;
            }

            var targetKey = ResolveOperandKey(key: operation.TargetKey, keyFrom: operation.TargetKeyFrom, tick: tick);
            if (!int.TryParse(s: targetKey, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var targetIndex)) {
                m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: effect, tick: tick, detail: $"target body key '{targetKey}' is invalid");
                return true;
            }
            if ((targetIndex == bodyIndex) || (Body(index: targetIndex) is null)) {
                var detail = ((targetIndex == bodyIndex)
                    ? $"body:{bodyIndex} cannot designate itself"
                    : $"target body:{targetIndex} is inactive"
                );
                m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyTargetInvalid, ruleName: ruleName, effect: effect, tick: tick, detail: detail);
                return true;
            }
            if (preflight) {
                return false;
            }
            _ = ApplyDesignationCore(
                designation: new WorldDesignation(EntityIndex: bodyIndex, Register: operation.Register!, Subject: GrantSubject.Body(index: targetIndex)),
                principal: WorldPrincipal.World,
                knownSubject: true,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0
            );
            return false;
        }

        if (preflight) {
            return false;
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

        return false;
    }
    private bool FireFieldPaint(PaintFieldEffect effect, string ruleName, ulong tick, bool preflight) {
        if (m_population.Fields is not { } lattice) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.FieldUnavailable, ruleName: ruleName, effect: effect, tick: tick, detail: "no live field lattice is installed");
            return true;
        }

        var paint = effect.Paint;
        if (!lattice.TryFieldIndex(name: paint.Field, field: out _)) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.FieldUnavailable, ruleName: ruleName, effect: effect, tick: tick, detail: $"live field '{paint.Field}' is unavailable");
            return true;
        }
        if (preflight) {
            return false;
        }
        _ = lattice.PaintSphere(
            fieldName: paint.Field,
            centerX: paint.X,
            centerY: paint.Y,
            centerZ: paint.Z,
            radius: paint.Radius,
            operation: (paint.Operation switch {
                WorldFieldWriteOp.Set => Puck.Physics.Fields.FieldWriteOp.Set,
                WorldFieldWriteOp.Add => Puck.Physics.Fields.FieldWriteOp.Add,
                _ => throw new ArgumentOutOfRangeException(paramName: nameof(paint.Operation), actualValue: paint.Operation, message: null),
            }),
            value: paint.Value
        );

        return false;
    }
    // The world's mutation door as the evaluator sees it. Outside preflight the ordinary pipeline installs, or
    // refuses by name through its own mutation rejection. Under preflight the candidate composes and validates
    // privately and becomes m_definition, so the next preflighted step reads it; the enclosing EndPreflight restores
    // the installed document. Transaction steps cannot add or remove state rows, so their compiled row ordinals
    // remain valid while cell values and keys move.
    private bool TryApplyRuleMutation(WorldMutation mutation, ulong tick, bool preflight, out string reason) {
        reason = string.Empty;

        if (!preflight) {
            if (TryApplyMutation(mutation: mutation, tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false)) {
                return true;
            }

            reason = "the ordinary mutation door refused the effect; its mutation rejection names the concrete reason";

            return false;
        }

        var current = m_definition;

        if (!TryCompose(current: current, mutation: mutation, tick: tick, instanceIdentity: InstanceIdentity, candidate: out var candidate, reason: out reason, evictedKey: out _, patterns: m_patterns)) {
            return false;
        }

        candidate = RebaseCellTraits(candidate: candidate, mutation: mutation, original: current, tick: tick);

        if (!TryValidateMutationCandidate(candidate: candidate, mutation: mutation, reason: out reason, compilation: out _, retainCompilation: false)) {
            return false;
        }

        if ((candidate.Adjacencies is { Count: > 0 }) && AdjacencyProofInputsChanged(candidate: candidate, current: current, mutation: mutation)) {
            reason = "the mutation changes an adjacency overlap input and requires world.load/world.reload";
        } else if (ExceedsBootDerivedFaceReservation(candidate: candidate, reason: out var reservationReason)) {
            reason = reservationReason;
        } else if (AffectsRenderEnvelope(mutation: mutation) && !m_envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
            reason = capacityReason;
        } else if (!m_population.CanInstallFields(definition: candidate, reason: out var fieldReason)) {
            reason = fieldReason!;
        } else if (AffectsSolidField(mutation: mutation) && !TryBuildSolids(definition: candidate, reason: out var solidReason, solids: out _)) {
            reason = solidReason!;
        }

        if (reason.Length > 0) {
            return false;
        }

        m_definition = candidate;
        if (m_preflightMutations.Count > 0) {
            m_preflightMutations.Peek().Add(item: mutation);
        }

        return true;
    }
    private bool TryApplyRuleMutation(EffectFact effect, string ruleName, WorldMutation mutation, ulong tick, bool preflight) {
        if (TryApplyRuleMutation(mutation: mutation, tick: tick, preflight: preflight, reason: out var reason)) {
            return true;
        }

        m_evaluator.ReportRefusal(refusal: RuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: effect, tick: tick, detail: reason);

        return false;
    }
    // Body state, not document state: the same WorldBody.Pose door ApplyCommand's SnapPose arm (body.pose) uses,
    // but as the world's own act — no drive-gate or grant check, since a gated body is one a rule still needs to
    // move.
    private bool FirePoseEffect(PoseEffect effect, string ruleName, ulong tick, bool preflight) {
        var poseEffect = effect;
        // A '$cell:' indirection yields the cell's integer, which may exceed int — a body index it can never name.
        var spelled = ResolveOperandKey(
            key: poseEffect.Key,
            keyFrom: poseEffect.KeyFrom,
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
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"key '{spelled}' is not a body index");

            return true;
        }

        var bodyIndex = ((int)resolved);

        if (Body(index: bodyIndex) is not { } body) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"body:{bodyIndex} is inactive");

            return true;
        }

        CompiledWorldPose pose;

        if (poseEffect.Pose is { } literal) {
            pose = literal;
        } else if (WorldDefinitionRows.FindSpawnPoint(
            spawnPoints: m_definition.SpawnPoints,
            id: poseEffect.SpawnPoint
        ) is { } point) {
            var spawn = FixedSpawnPoint.Compile(point: in point);

            pose = new CompiledWorldPose(
                Position: spawn.Position,
                YawRadians: spawn.YawRadians,
                PitchRadians: FixedQ4816.Zero,
                RollRadians: FixedQ4816.Zero
            );
        } else {
            if (m_output.HasNarrationSink) {
                m_output.Narrate(channel: "world.rule", text: $"[world.rule: pose skipped — spawnPoint '{poseEffect.SpawnPoint}' is no longer declared]");
            }

            return false;
        }

        if (preflight) {
            return false;
        }
        body.Pose(
            position: pose.Position,
            yawRadians: pose.YawRadians,
            pitchRadians: pose.PitchRadians,
            rollRadians: pose.RollRadians
        );
        if (m_output.HasNarrationSink) {
            m_output.Narrate(channel: "world.rule", text: $"[world.rule: pose body:{bodyIndex} -> ({pose.Position.X}, {pose.Position.Y}, {pose.Position.Z})]");
        }

        return false;
    }
    // Recompiles the rules section and prunes the edge latch to the surviving names. The compiler is called here
    // UNWRAPPED because WorldDefinitionValidator already compiled this exact candidate and refused it if it could
    // not — the same trusted-second-call shape every other derived-state rebuild in Install has.
    //
    // This is also every install's one derived-board fix-up point: boot, Install, and checkpoint restore all call
    // this before anything else reads the document, so a hand-authored or restored definition whose derived boards
    // have drifted from their tokens/codes rows never survives past this call — RecomposeDerivedBoards is a no-op
    // for the ordinary case where the live mutation pipeline already composed them correctly.
    private WorldDefinition RecompileRules(WorldDefinition definition, WorldRuleCompilation? compilation = null) {
        definition = RecomposeDerivedBoards(definition: definition);
        m_definition = definition;
        // Recomposition may change dependencies: only the exact definition can reuse its validation result.
        if (!ReferenceEquals(compilation?.Definition, definition)) { compilation = WorldRuleCompilation.Compile(definition); }
        m_tables = compilation!.Tables;
        m_rules = compilation.Rules;
        m_interactions = compilation.Interactions;

        m_ruleGateHeld.Prune(compiled: m_rules);
        m_interactionGateHeld.Prune(compiled: m_interactions);
        PruneBoardEnforcement(definition: definition);
        ReconcileDecisions();
        ReconcilePatterns(definition);

        if (!WorldSearchCompilation.TryPlanAll(definition: definition, rules: m_rules, plans: out var searchPlans, judge: out var searchJudge, reason: out var searchReason)) {
            throw new InvalidOperationException(message: $"search failed to plan after validation: {searchReason}");
        }

        m_search.Rebuild(
            plans: searchPlans, judge: searchJudge, rows: definition.State, catalog: definition.StateCatalog,
            topology: name => WorldTopologyCompilation.Find(definition, name), patterns: m_patterns, tables: m_tables
        );
        m_population.BindFlockAffinities(definition, EvaluateFlockAffinity);

        return definition;
    }
    // The search jobs advance right after the rules, so a job judges the position this tick's rules settled and a
    // finished job's outputs are delivered with the same tick.
    private ulong m_searchTick;
    private void StepSearch(ulong tick) {
        m_searchTick = tick;

        if (m_search.Step(tick: tick, apply: m_searchApply)) {
            DeliverPending();
        }
    }
    /// <summary>Lists every search job's progress.</summary>
    public IReadOnlyList<SearchStatus> SearchStatus() => m_search.Status();
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
        DrainRecordedExtensions();
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
                if (m_output.HasNarrationSink) {
                    m_output.Narrate(channel: "world.engage", text: $"[world.engage: {principal.Describe()} auto-engaged {target.Describe()} — context button]");
                }
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
                edges: ProjectSenseEdges(),
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
        StepBoardEnforcement(tick: tick);
        StepSearch(tick: tick);
        StepFields(tick: tick);
        // Escrow, transfer, and contribution deadline recovery evaluate on the SAME terms, right beside rules — see
        // SweepDeadlines' own remarks.
        SweepDeadlines(tick: tick);
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
                var actor = submission.Principal;
                var entityIndex = submission.EntityIndex;

                if (m_output.HasNarrationSink) {
                    m_output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {verdict.DescribeRefusal(
                        actor: actor,
                        dropped: "intent dropped, body idle",
                        subject: $"body:{entityIndex}",
                        verb: "drive"
                    )}]");
                }
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
                if (m_output.HasNarrationSink) {
                    m_output.Narrate(channel: "world.server-event refused", text: $"[world.server-event refused: {serverEvent.GetType().Name} is not declared]");
                }
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
        lock (m_authorityGate) {
            m_mutationBudget.BeginTick();
            DrainRecordedExtensions();
            return DrainPendingOps(tick: m_lastCompletedTick);
        }
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
    // One entry in the ordered domain (see m_ordered's own remarks): the envelope plus the completion its submitter
    // supplied (null when the caller does not need one).
    private abstract record OrderedEntry {
        public sealed record Submission(SubmissionEnvelope Envelope, Action<WorldSubmissionResult>? Completion) : OrderedEntry;
        public sealed record ServerEvent(WorldServerEvent Value) : OrderedEntry;
    }
}
