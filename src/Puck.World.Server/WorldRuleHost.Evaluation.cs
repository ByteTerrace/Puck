using Puck.Maths;
using RuleNeeds = Puck.State.RuleNeeds;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    // Every (left carrier, right carrier) pair within range, or every left carrier inside the region, fires the
    // interaction once with left/right bound — the chemistry is evaluated over all carriers, never one argmax pair.
    private bool EvaluateInteraction(CompiledWorldFactsRule rule, CompiledInteraction interaction, RuleLatch latch, Dictionary<LatchKey, bool> bindings, ulong stepTicks) {
        var applied = false;
        var lefts = m_carrierScratchLeft;

        Carriers(
            into: lefts,
            rowOrdinal: CarrierRowOrdinal(name: interaction.Left)
        );
        latch.BeginSweep();

        if (interaction.CoOccurrence == WorldInteractionCoOccurrence.Region) {
            foreach (var left in lefts) {
                if (!Host.Events.IsOccupant(
                    body: left,
                    placementId: interaction.Right
                )) {
                    continue;
                }

                m_boundLeft = left;
                m_boundRight = -1;
                _ = m_evaluator.EvaluateOnce(
                    applied: out var fired,
                    binding: new LatchKey(
                        Left: left,
                        Right: -1
                    ),
                    bindings: bindings,
                    latch: latch,
                    rule: rule,
                    stepTicks: stepTicks
                );
                applied |= fired;
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
                rowOrdinal: CarrierRowOrdinal(name: interaction.Right)
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

                        while (
                            (slot > 0) &&
                            ((m_neighbourDistance[(slot - 1)] > distanceSquared) || ((m_neighbourDistance[(slot - 1)] == distanceSquared) && (m_neighbourIndex[(slot - 1)] > right)))
                        ) {
                            if (slot < budget) {
                                m_neighbourDistance[slot] = m_neighbourDistance[(slot - 1)];
                                m_neighbourIndex[slot] = m_neighbourIndex[(slot - 1)];
                            }
                            slot--;
                        }
                        if (slot < budget) {
                            m_neighbourDistance[slot] = distanceSquared;
                            m_neighbourIndex[slot] = right;
                            kept = Math.Min(
                                val1: (kept + 1),
                                val2: budget
                            );
                        }

                        continue;
                    }

                    m_boundLeft = left;
                    m_boundRight = right;
                    _ = m_evaluator.EvaluateOnce(
                        applied: out var pairFired,
                        binding: new LatchKey(
                            Left: left,
                            Right: right
                        ),
                        bindings: bindings,
                        latch: latch,
                        rule: rule,
                        stepTicks: stepTicks
                    );
                    applied |= pairFired;
                }

                for (var index = 0; (index < kept); index++) {
                    var right = m_neighbourIndex[index];

                    m_boundLeft = left;
                    m_boundRight = right;
                    _ = m_evaluator.EvaluateOnce(
                        applied: out var pairFired,
                        binding: new LatchKey(
                            Left: left,
                            Right: right
                        ),
                        bindings: bindings,
                        latch: latch,
                        rule: rule,
                        stepTicks: stepTicks
                    );
                    applied |= pairFired;
                }
            }
        }

        m_boundLeft = -1;
        m_boundRight = -1;
        latch.EndSweep(bindings: bindings);

        return applied;
    }

    // Resolves a document row once through the catalog's name -> handle dictionary (StateCatalog.TryResolve) and the
    // handle's own LaneOrdinal as a direct row-array index, instead of WorldDefinitionRows.FindStateRow's linear scan
    // over every declared row. Shared by every tick-path reader that starts from a row name (Carriers/CarrierKeys
    // below, WorldServer.BoardEnforcement.cs) — the handle is handed back too, so a caller that walks the row's own
    // cells afterward reads each one through it rather than resolving the row by name again per cell.
    internal bool TryResolveDocumentRow(string name, out StateHandle handle, out WorldStateRow? row) {
        var catalog = Host.Arena.Catalog;

        if (
            catalog.TryResolve(
            handle: out handle,
            lane: StateLane.Document,
            name: name
        ) &&
            (((uint)catalog.Descriptors[handle.Ordinal].LaneOrdinal) < ((uint)Host.Definition.State.Count))
        ) {
            row = Host.Definition.State[catalog.Descriptors[handle.Ordinal].LaneOrdinal];

            return true;
        }

        row = null;

        return false;
    }

    // The catalog ordinal of a row a carrier sweep iterates, or -1.
    private int CarrierRowOrdinal(string name) => (Host.Arena.Catalog.TryResolve(
        handle: out var handle,
        lane: StateLane.Document,
        name: name
    )
        ? handle.Ordinal
        : -1
    );
    // The integer keys a keyed row holds at this moment, ascending — the iteration set of a decision rule.
    private void CarrierKeys(int rowOrdinal, List<int> into) {
        into.Clear();

        if (rowOrdinal < 0) {
            into.Sort();

            return;
        }

        var count = Host.Arena.CellCount(rowOrdinal: rowOrdinal);

        for (var position = 0; (position < count); position++) {
            if (
                Host.Arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            ) &&
                StateReader.TryParseCandidateIndex(
                index: out var index,
                key: Host.Arena.Keys[key]
            )
            ) {
                into.Add(item: index);
            }
        }

        into.Sort();
    }
    // The active bodies whose cell in a keyed tag row reads nonzero, ascending, into the caller's scratch list.
    private void Carriers(int rowOrdinal, List<int> into) {
        into.Clear();

        if (rowOrdinal < 0) {
            return;
        }

        var count = Host.Arena.CellCount(rowOrdinal: rowOrdinal);

        for (var position = 0; (position < count); position++) {
            if (
                !Host.Arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            ) ||
                !StateReader.TryParseCandidateIndex(
                index: out var index,
                key: Host.Arena.Keys[key]
            ) ||
                (Host.Body(index: index) is null) ||
                (Host.ReadArenaCell(
                key: key,
                rowOrdinal: rowOrdinal
            ) == FixedQ4816.Zero)
            ) {
                continue;
            }

            into.Add(item: index);
        }

        into.Sort();
    }

    // Evaluates every compiled rule's gate and fires its effects in document order, then every compiled
    // interaction's on the same terms. That ordering is the same-tick effect tiebreak: a rule can set up a fact an
    // interaction's gate reads this tick, and two interactions cascade in their own declared order.
    //
    // The arena is the authoritative state while they run; the installed document becomes the arena's export once
    // they are done, and one delivery follows a tick that moved anything.
    internal void EvaluateWorldRules(ulong tick, ulong stepTicks) {
        m_decisionWork = default;
        AdvanceHost(
            engineTick: Host.CompletedEngineTicks,
            tick: tick
        );
        SyncIdentityFactLanes();
        FreezeDecisionPerception(rules: m_rules);

        var applied = m_evaluator.Evaluate(
            latch: m_ruleGateHeld,
            rules: m_ungroupedRules,
            stepTicks: stepTicks
        );

        if (m_groups.Length != 0) {
            applied |= m_evaluator.EvaluateGroups(
                groups: m_groups,
                latch: m_ruleGateHeld,
                rules: m_rules,
                state: m_groupState,
                stepTicks: stepTicks
            );
        }

        applied |= m_evaluator.Evaluate(
            latch: m_interactionGateHeld,
            rules: m_interactions,
            stepTicks: stepTicks
        );
        FlushIdentityFacts();
        applied |= Host.PublishArena();
        if (applied) {
            Host.Document.DeliverPending();
        }
    }

    // Every rule, every group trigger, and every decision the server installs is admitted against this host once,
    // at compile time. Nothing on the tick path asks again.
    /// <summary>Admits every compiled rule against this host, throwing when one names a need the host cannot
    /// answer.</summary>
    /// <param name="rules">The compiled rules.</param>
    /// <param name="subject">The noun a refusal names them by.</param>
    /// <exception cref="InvalidOperationException">A rule cannot run on this host.</exception>
    private void AdmitRules(CompiledRule[] rules, string subject) {
        foreach (var rule in rules) {
            if (!RuleNeeds.Admit(
                needs: rule.Needs,
                reader: this,
                refusal: out var refusal
            )) {
                throw new InvalidOperationException(message: $"{subject} '{rule.Name}' cannot run on this host: {refusal}");
            }
        }
    }
    // A group's own needs are its trigger's: the gate that decides whether the group runs at all reads through this
    // host exactly as a rule's gate does, so it is admitted here beside them.
    /// <summary>Admits every compiled group's trigger against this host.</summary>
    /// <param name="groups">The compiled groups.</param>
    /// <exception cref="InvalidOperationException">A group cannot run on this host.</exception>
    private void AdmitGroups(CompiledRuleGroup[] groups) {
        foreach (var group in groups) {
            if (!RuleNeeds.Admit(
                needs: group.Needs,
                reader: this,
                refusal: out var refusal
            )) {
                throw new InvalidOperationException(message: $"group '{group.Name}' cannot run on this host: {refusal}");
            }
        }
    }
}
