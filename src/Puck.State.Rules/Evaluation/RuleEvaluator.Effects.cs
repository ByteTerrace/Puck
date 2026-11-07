using System.Globalization;

namespace Puck.State.Rules;

public sealed partial class RuleEvaluator {
    private readonly List<IRuleEffect> m_deferred = [];
    // What each queued arm's declared sources read, in one buffer: the slot a queued arm owns starts at its entry in
    // m_deferredValues, and the same values answer its preflight and its delivery.
    private readonly List<int> m_deferredValues = [];
    private Outcome[] m_armOutcomes = new Outcome[16];

    private int m_armOutcomeCount;

    // What firing one effect, or a sequence of them, answered. The zero value is a fault, so a step nothing set refuses
    // the firing as a fault does: a faulted step decides nothing, and the firing it belongs to is Faulted rather than
    // Refused.
    private enum Step : byte {
        Faulted = 0,
        Refused,
        Done,
    }
    // What one firing proposes: its scope's mark, how many arms were queued below it, and what its steps folded to.
    private readonly record struct Proposal(FiringDecision Decision, int Mark, int Queued);

    private static long ScheduleDueTick(ulong tick, long delayTicks, out ExpressionFault fault) {
        var failed = ((delayTicks < 0L) || (tick > ((ulong)(long.MaxValue - Math.Max(
            val1: 0L,
            val2: delayTicks
        )))));

        fault = (failed
            ? ExpressionFault.Domain
            : ExpressionFault.None
        );

        return (failed
            ? 0L
            : checked((((long)tick) + delayTicks))
        );
    }
    // One firing is one journal scope. Every reversible effect lands in it; an irreversible arm is queued at
    // whatever depth it sits, validated against the state the scope proposes to commit, and fired only after the
    // commit. A refusal anywhere before the commit rewinds the scope and discards the queue, and so does anything
    // thrown out of an effect, a host, or an arm's preflight: the scope never outlives the firing, whichever way
    // the firing ends. A rewound firing moved nothing, and says so.
    private RuleOutcome FireRule(CompiledRule rule, ulong tick, ulong stepTicks, out bool applied) => FireEffects(
        applied: out applied,
        effects: rule.Effects,
        ruleName: rule.Name,
        stepTicks: stepTicks,
        tick: tick
    );

    /// <summary>Fires one effect sequence as one firing: one journal scope, committed when every required effect
    /// succeeds and rewound otherwise, with each irreversible arm queued and fired after the commit.</summary>
    /// <param name="effects">The effects, in authored order.</param>
    /// <param name="ruleName">The rule a refusal names.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">The engine ticks this step advances.</param>
    /// <param name="applied">Whether the firing moved the arena.</param>
    /// <param name="prelude">What the host records into the scope the moment it opens, or <see langword="null"/>: the
    /// state its firing decided beside the cells, which then rewinds with them.</param>
    /// <returns><see cref="RuleOutcome.Fired"/> when the scope committed, <see cref="RuleOutcome.Refused"/> when an
    /// effect refused and <see cref="RuleOutcome.Faulted"/> when one faulted; either rewinds the scope.</returns>
    /// <remarks>A host evaluating a rule kind of its own — a decision's option or its no-choice arm — fires through
    /// here so its effects are one scope, exactly as a rule's own are.</remarks>
    [Replaces(root: typeof(StateArena))]
    public RuleOutcome FireEffects(IRuleEffect[] effects, string ruleName, ulong tick, ulong stepTicks, out bool applied, IFiringPrelude? prelude = null) {
        ArgumentNullException.ThrowIfNull(argument: effects);

        if (effects is [RewindGroupEffect rewind]) {
            var reason = "host has no retained undo arena";

            applied = ((m_host is IArenaUndoHost undo) && undo.TryRewindGroup(group: rewind.Group, reason: out reason));
            if (!applied) {
                ReportRefusal(detail: ((m_host is IArenaUndoHost) ? reason : "host has no retained undo arena"), effect: rewind.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: ruleName, tick: tick);
                return RuleOutcome.Refused;
            }
            return RuleOutcome.Fired;
        }

        var firing = new EffectFiring(
            EngineTick: m_host.EngineTick,
            RuleName: ruleName,
            StepTicks: stepTicks,
            Tick: tick
        );
        var queued = m_deferred.Count;
        var mark = m_host.BeginStateTransaction();

        applied = false;

        // An effect that meets a fault answers Faulted and one that refuses answers Refused; either rewinds the firing.
        Step proposed;

        try {
            prelude?.Record(journal: m_host.Arena.Journal);
            proposed = ProposeEffects(
                applied: ref applied,
                effects: effects,
                firing: in firing,
                queued: queued,
                ruleName: ruleName
            );
        } catch {
            _ = Commit(
                applied: ref applied,
                firing: in firing,
                proposal: new Proposal(
                    Decision: FiringDecision.Faulted,
                    Mark: mark,
                    Queued: queued
                )
            );

            throw;
        }

        return Commit(
            applied: ref applied,
            firing: in firing,
            proposal: new Proposal(
                Decision: ((proposed == Step.Done)
                    ? FiringDecision.Fires
                    : ((proposed == Step.Faulted)
                        ? FiringDecision.Faulted
                        : FiringDecision.Refused)),
                Mark: mark,
                Queued: queued
            )
        );
    }

    // The one door a firing's scope closes through, switching on what the firing decided: a faulted or refused firing
    // rewinds cells, edge bit and decision state together and discards what it queued; a gate that closed or an Edge that
    // was held commits the scope with only its bookkeeping in it; a firing that fires commits, installs the
    // transactional arms and delivers the rest.
    private RuleOutcome Commit(in Proposal proposal, in EffectFiring firing, ref bool applied) {
        var mark = proposal.Mark;
        var queued = proposal.Queued;

        switch (proposal.Decision) {
            case FiringDecision.Closed:
            case FiringDecision.Held:
                // No effect ran, so nothing but the edge bit is in the scope and no host has anything to resynchronise.
                m_host.Arena.Commit(mark: mark);

                return RuleOutcome.Idle;
            case FiringDecision.Fires:
                break;
            default:
                m_host.RewindStateTransaction(mark: mark);
                DiscardQueued(from: queued);
                applied = false;

                return ((proposal.Decision == FiringDecision.Faulted)
                    ? RuleOutcome.Faulted
                    : RuleOutcome.Refused);
        }

        var ruleName = firing.RuleName;
        var tick = firing.Tick;

        m_host.CommitStateTransaction(mark: mark);

        // An arm is the only work during rule evaluation that changes host state (a pose, a spawn, an install), so a
        // firing that queued one brings the host's fact versions up to date once its arms are delivered; arena writes
        // move row versions on their own.
        var armed = (m_deferred.Count > queued);

        // The scope is closed. The transactional arms install first, as the unit the host prepared, and the rest are
        // delivered in authored order: a delivery that refuses is counted, undoes nothing, and stops no later
        // delivery. Every firing reads the queue from its own mark up, so an arm left below it would never
        // fire again and never leave: the queue is emptied however the firing ends.
        try {
            m_host.Committed(scope: mark);

            if (QueuedTransactional(from: queued)) {
                m_host.CommitTransactional(firing: in firing);
                applied = true;
            }

            for (var index = queued; (index < m_deferred.Count); index++) {
                var arm = m_deferred[index];

                if ((arm.Needs & EffectNeeds.Transactional) != EffectNeeds.None) {
                    continue;
                }

                if (Deliver(
                    arm: arm,
                    firing: (firing with { Values = ValuesOf(index: index) }),
                    refusal: out var refusal
                )) {
                    applied |= arm.SubmitsMutation;
                } else {
                    ReportRefusal(
                        effect: arm.Describe,
                        fallback: RuleEffectRefusal.IrreversibleArmFailed,
                        refusal: in refusal,
                        ruleName: ruleName,
                        tick: tick
                    );
                }
            }
        } finally {
            DiscardQueued(from: queued);
            if (armed) {
                m_host.RefreshFacts();
            }
        }

        return RuleOutcome.Fired;
    }
    // Delivers one irreversible arm after its firing's commit. Past the commit nothing rewinds, so an arm whose
    // evaluation faulted refuses its own delivery.
    private bool Deliver(IRuleEffect arm, in EffectFiring firing, out EffectRefusal refusal) => arm.TryDeliverAfterCommit(
        firing: in firing,
        host: m_host,
        refusal: out refusal
    );
    // Everything a firing does before its commit: the reversible effects, then each queued arm's preflight against
    // the state they propose, then the host's preparation of the transactional arms as one unit. False means
    // refused, already reported.
    private Step ProposeEffects(IRuleEffect[] effects, string ruleName, in EffectFiring firing, int queued, ref bool applied) {
        var sequence = FireSequence(
            applied: ref applied,
            effects: effects,
            firing: in firing,
            strict: false
        );

        if (sequence != Step.Done) {
            return sequence;
        }

        var preflight = (firing with { Preflight = true });

        if (m_deferred.Count > queued) {
            m_host.Preflighting();
        }

        for (var index = queued; (index < m_deferred.Count); index++) {
            var arm = m_deferred[index];
            var read = ReadSources(
                arm: arm,
                firing: in firing,
                start: m_deferredValues[index]
            );

            if (read != Step.Done) {
                return read;
            }

            if (!arm.TryFire(
                firing: (preflight with { Values = ValuesOf(index: index) }),
                host: m_host,
                refusal: out var refusal
            )) {
                ReportRefusal(
                    effect: arm.Describe,
                    fallback: RuleEffectRefusal.MutationRejected,
                    refusal: in refusal,
                    ruleName: ruleName,
                    tick: firing.Tick
                );

                return Step.Refused;
            }
        }

        if (
            QueuedTransactional(from: queued) &&
            !m_host.PrepareTransactional(
                firing: in preflight,
                refusal: out var unitRefusal
            )
        ) {
            ReportRefusal(
                effect: "the firing's transactional arms",
                fallback: RuleEffectRefusal.MutationRejected,
                refusal: in unitRefusal,
                ruleName: ruleName,
                tick: firing.Tick
            );

            return Step.Refused;
        }

        if (!m_host.PrepareStateCommit(refusal: out var stateRefusal)) {
            ReportRefusal(effect: "the firing's state transaction", fallback: RuleEffectRefusal.MutationRejected,
                refusal: in stateRefusal, ruleName: ruleName, tick: firing.Tick);
            return Step.Refused;
        }
        return Step.Done;
    }
    private bool QueuedTransactional(int from) {
        for (var index = from; (index < m_deferred.Count); index++) {
            if ((m_deferred[index].Needs & EffectNeeds.Transactional) != EffectNeeds.None) {
                return true;
            }
        }

        return false;
    }
    private void DiscardQueued(int from) {
        if (from < m_deferred.Count) {
            m_armOutcomeCount = m_deferredValues[from];
        }

        m_deferred.RemoveRange(
            count: (m_deferred.Count - from),
            index: from
        );
        m_deferredValues.RemoveRange(
            count: (m_deferredValues.Count - from),
            index: from
        );
    }
    private int ReserveValues(int count) {
        var start = m_armOutcomeCount;

        if ((start + count) > m_armOutcomes.Length) {
            Array.Resize(
                array: ref m_armOutcomes,
                newSize: Math.Max(
                    val1: (start + count),
                    val2: (m_armOutcomes.Length * 2)
                )
            );
        }

        m_armOutcomeCount = (start + count);

        return start;
    }
    // The values a queued arm's sources read, as the view its calls are handed.
    private EffectValues ValuesOf(int index) => ((m_deferred[index].Sources.Length == 0)
        ? default
        : new EffectValues(
            count: m_deferred[index].Sources.Length,
            start: m_deferredValues[index],
            values: m_armOutcomes
        ));
    // Reads an arm's declared sources into the slots from `start`, in declared order. A source that faulted decided
    // nothing: the firing is faulted, and the refusal names the source. An absence is a value the arm answers.
    private Step ReadSources(IRuleEffect arm, int start, in EffectFiring firing) {
        var sources = arm.Sources;

        for (var position = 0; (position < sources.Length); position++) {
            var outcome = ReadSource(source: in sources[position]);

            m_armOutcomes[(start + position)] = outcome;
            if (outcome.Case == FaultCase.Faulted) {
                ReportRefusal(
                    detail: $"source {position.ToString(provider: CultureInfo.InvariantCulture)} faulted: {RuleEvaluation.DescribeOutcome(outcome: outcome)}",
                    effect: arm.Describe,
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: firing.RuleName,
                    tick: firing.Tick
                );

                return Step.Faulted;
            }
        }

        return Step.Done;
    }
    private Outcome ReadSource(in EffectSource source) {
        switch (source.Kind) {
            case EffectSourceKind.Key: {
                    var resolved = RuleReads.ResolveKey(
                        keyFrom: source.KeyFrom,
                        literal: source.Literal,
                        reader: m_host
                    );

                    if (resolved.Case == FaultCase.Faulted) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    return Outcome.Finite(raw: ((resolved.TryNamed(value: out var key) && RuleReads.TryKeyIndex(
                        index: out var index,
                        keys: m_host.Arena.Keys,
                        key: key
                    ))
                        ? index
                        : -1L));
                }
            case EffectSourceKind.Program: {
                    var frame = new EvaluationFrame(reader: m_host);

                    return RuleExpressions.Evaluate(
                        frame: ref frame,
                        kind: source.ValueKind,
                        program: source.Program!
                    );
                }
            default: {
                    var fact = source.Operand!.Read(reader: m_host);

                    return ((fact.Outcome.Case == FaultCase.Finite)
                        ? Outcome.Finite(raw: fact.ToRaw(kind: source.ValueKind))
                        : fact.Outcome);
                }
        }
    }

    /// <summary>Evaluates one expression program for a host that scores by it: the decision's option score, read
    /// under the binding the host holds. The program is evaluated here, never by the host.</summary>
    /// <param name="program">The program.</param>
    /// <param name="kind">The encoding the program answers in.</param>
    /// <returns>What the program answered: a number, an absence, or a fault.</returns>
    public Outcome Score(CompiledExpressionToken[] program, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var frame = new EvaluationFrame(reader: m_host);

        return RuleExpressions.Evaluate(
            frame: ref frame,
            kind: kind,
            program: program
        );
    }

    // Fires one effect sequence in order, returning false on the first refusal. An arm that cannot be rewound is
    // queued rather than fired, at any depth.
    private Step FireSequence(IRuleEffect[] effects, in EffectFiring firing, bool strict, ref bool applied) {
        var trace = m_traceEntry;

        for (var index = 0; (index < effects.Length); index++) {
            var effect = effects[index];

            if ((effect.Needs & EffectNeeds.Irreversible) != EffectNeeds.None) {
                m_deferred.Add(item: effect);
                m_deferredValues.Add(item: ReserveValues(count: effect.Sources.Length));
                trace?.Effects.Add(item: $"{effect.Describe}: queued");

                continue;
            }

            var serial = m_refusalSerial;
            var fired = FireOne(
                effect: effect,
                firing: in firing,
                moved: out var moved,
                strict: strict
            );

            applied |= moved;
            trace?.Effects.Add(item: DescribeTracedEffect(
                applied: moved,
                effect: effect,
                refused: (m_refusalSerial != serial)
            ));
            if (fired != Step.Done) {
                return fired;
            }

            // Every write a scope holds is a record the rewind needs, so the record is what a firing is bounded by:
            // checked between effects, it refuses the sequence the way any refused effect does.
            if (m_host.Arena.Journal.OverCeiling) {
                ReportRefusal(
                    detail: $"its writes hold {m_host.Arena.Journal.Bytes.ToString(provider: CultureInfo.InvariantCulture)} bytes of undo record, past the {ArenaCapacity.MaxJournalBytes.ToString(provider: CultureInfo.InvariantCulture)}-byte ceiling; write fewer cells in one firing, or split the work across rules",
                    effect: effect.Describe,
                    refusal: RuleEffectRefusal.JournalCeiling,
                    ruleName: firing.RuleName,
                    tick: firing.Tick
                );

                return Step.Refused;
            }
        }

        return Step.Done;
    }
    // Returns false only when the effect refused, which rewinds the enclosing scope; an effect that fired without
    // moving the arena, or that could not move its destination, returns true with `moved` false.
    private Step FireOne(IRuleEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        switch (effect) {
            case ClaimEffect claim:
                return FireClaim(effect: claim, firing: in firing, moved: out moved, strict: strict);
            case ClaimPairEffect pair:
                return FireClaimPair(effect: pair, firing: in firing, moved: out moved, strict: strict);
            case ReleaseEffect release:
                return FireRelease(effect: release, firing: in firing, moved: out moved);
            case ForEachPoolEffect each:
                return FireForEachPool(effect: each, firing: in firing, moved: out moved, strict: strict);
            case InstanceFieldWriteEffect fieldWrite:
                return FireInstanceFieldWrite(effect: fieldWrite, firing: in firing, moved: out moved);
            case StaticInstanceFieldWriteEffect staticWrite:
                return FireStaticInstanceFieldWrite(effect: staticWrite, firing: in firing, moved: out moved);
            case InstanceFieldScheduleEffect fieldSchedule:
                return FireInstanceFieldSchedule(effect: fieldSchedule, firing: in firing, moved: out moved);
            case InstanceFieldVectorWriteEffect vectorWrite:
                return FireInstanceFieldVectorWrite(effect: vectorWrite, firing: in firing, moved: out moved);
            case IfEffect branch:
                return FireIf(
                    effect: branch,
                    firing: in firing,
                    moved: out moved,
                    strict: strict
                );
            case TransactionEffect savepoint:
                return FireSavepoint(
                    effect: savepoint,
                    firing: in firing,
                    moved: out moved
                );
            case TransformStateEffect transform:
                return FireTransform(
                    effect: transform,
                    firing: in firing,
                    moved: out moved
                );
            case VectorCopyEffect or VectorMixEffect or VectorMeanEffect or VectorNearestEffect or VectorRememberEffect:
                return FireVector(
                    effect: ((VectorEffect)effect),
                    firing: in firing,
                    moved: out moved
                );
            case PushStateEffect push:
                return FirePush(
                    effect: push,
                    firing: in firing,
                    moved: out moved
                );
            case GenerateEffect generate:
                return Apply(
                    effect: effect,
                    firing: in firing,
                    moved: out moved,
                    mutation: Mutation.Generate(rowOrdinal: generate.RowOrdinal)
                );
            case RemoveStateCellEffect remove:
                return FireRemove(
                    effect: remove,
                    firing: in firing,
                    moved: out moved,
                    strict: strict
                );
            case IStateWriteEffect write:
                return FireWrite(
                    effect: effect,
                    firing: in firing,
                    moved: out moved,
                    strict: strict,
                    write: write
                );
            default:
                return FireArm(
                    effect: effect,
                    firing: in firing,
                    moved: out moved
                );
        }
    }
    private Step FireClaim(ClaimEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if (((uint)effect.BindingSlot) >= ((uint)bindings.Length)) {
            ReportRefusal(detail: "this host exposes no pool-instance binding register", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (!m_host.Arena.TryClaim(poolOrdinal: effect.Pool.Ordinal, time: m_host.Time, handle: out var handle, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        var previous = bindings[effect.BindingSlot];

        bindings[effect.BindingSlot] = handle;
        try {
            var bodyMoved = false;

            var sequence = FireSequence(effects: effect.Effects, firing: in firing, strict: strict, applied: ref bodyMoved);

            if (sequence != Step.Done) {
                return sequence;
            }

            moved = true;
            return Step.Done;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private Step FireClaimPair(ClaimPairEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if (
            (((uint)effect.BindingSlot) >= ((uint)bindings.Length)) ||
            (((uint)effect.LeftBindingSlot) >= ((uint)bindings.Length)) ||
            (((uint)effect.RightBindingSlot) >= ((uint)bindings.Length)) ||
            (bindings[effect.LeftBindingSlot].PoolOrdinal != effect.LeftPool.Ordinal) ||
            (bindings[effect.RightBindingSlot].PoolOrdinal != effect.RightPool.Ordinal)
        ) {
            ReportRefusal(detail: "a pair endpoint binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (!m_host.Arena.TryClaimPair(
            poolOrdinal: effect.Pool.Ordinal,
            leftHandle: bindings[effect.LeftBindingSlot],
            rightHandle: bindings[effect.RightBindingSlot],
            time: m_host.Time,
            handle: out var handle,
            reason: out var reason
        )) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }

        var previous = bindings[effect.BindingSlot];

        bindings[effect.BindingSlot] = handle;
        try {
            var bodyMoved = false;

            var sequence = FireSequence(effects: effect.Effects, firing: in firing, strict: strict, applied: ref bodyMoved);

            if (sequence != Step.Done) {
                return sequence;
            }
            moved = true;
            return Step.Done;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private Step FireRelease(ReleaseEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the released pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (!m_host.Arena.TryRelease(handle: bindings[effect.BindingSlot], reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        moved = true;
        return Step.Done;
    }
    private Step FireForEachPool(ForEachPoolEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if (((uint)effect.BindingSlot) >= ((uint)bindings.Length)) {
            ReportRefusal(detail: "this host exposes no pool-instance binding register", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        var snapshot = PoolHandles(pool: effect.Pool, bindingSlot: effect.BindingSlot);
        var previous = bindings[effect.BindingSlot];

        try {
            foreach (var handle in snapshot) {
                if (!m_host.Arena.TryResolve(handle: handle, position: out _)) {
                    continue;
                }

                bindings[effect.BindingSlot] = handle;
                var sequence = FireSequence(effects: effect.Effects, firing: in firing, strict: strict, applied: ref moved);

                if (sequence != Step.Done) {
                    return sequence;
                }
            }
            return Step.Done;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private Step FireInstanceFieldWrite(InstanceFieldWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        var handle = bindings[effect.BindingSlot];

        if (effect.Field.Kind == CellKind.Text) {
            var text = effect.Text;

            if ((text is null) && !TryReadText(effect: effect, firing: in firing, source: effect.Source.Operand, step: out var textStep, text: out text)) {
                return textStep;
            }
            if (!m_host.Arena.TryWrite(handle: handle, fieldOrdinal: effect.Field.Ordinal, value: CellValue.Text(value: text), reason: out var textReason)) {
                ReportRefusal(detail: textReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return Step.Refused;
            }
        } else {
            if (!TryReadValue(effect: effect, source: effect.Source, kind: effect.Field.Kind, firing: in firing, raw: out var raw, step: out var read)) {
                return read;
            }

            if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: effect.Write, time: m_host.Time, reason: out var numericReason)) {
                ReportRefusal(detail: numericReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return Step.Refused;
            }
        }
        moved = true;
        return Step.Done;
    }
    private Step FireStaticInstanceFieldWrite(StaticInstanceFieldWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        if (!m_host.Arena.TryResolvePoolSlot(poolOrdinal: effect.Pool.Ordinal, slot: effect.Handle.Slot, handle: out var handle)) {
            ReportRefusal(detail: $"State pool '{effect.Pool.Name.Value}' slot {effect.Handle.Slot} is absent.", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (effect.Field.Kind == CellKind.Text) {
            var text = effect.Text;

            if ((text is null) && !TryReadText(effect: effect, firing: in firing, source: effect.Source.Operand, step: out var textStep, text: out text)) {
                return textStep;
            }
            if (!m_host.Arena.TryWrite(handle: handle, fieldOrdinal: effect.Field.Ordinal, value: CellValue.Text(value: text), reason: out var textReason)) {
                ReportRefusal(detail: textReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return Step.Refused;
            }
        } else {
            if (!TryReadValue(effect: effect, source: effect.Source, kind: effect.Field.Kind, firing: in firing, raw: out var raw, step: out var read)) {
                return read;
            }
            if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: effect.Write, time: m_host.Time, reason: out var reason)) {
                ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return Step.Refused;
            }
        }
        moved = true;
        return Step.Done;
    }
    private Step FireInstanceFieldSchedule(InstanceFieldScheduleEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        StateInstanceHandle handle;

        if (effect.BindingSlot >= 0) {
            var bindings = m_host.InstanceBindings;

            if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
                ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return Step.Refused;
            }
            handle = bindings[effect.BindingSlot];
        } else if (!m_host.Arena.TryResolvePoolSlot(poolOrdinal: effect.Pool.Ordinal, slot: effect.StaticSlot, handle: out handle)) {
            ReportRefusal(detail: $"State pool '{effect.Pool.Name.Value}' slot {effect.StaticSlot} is absent.", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        var raw = ScheduleDueTick(delayTicks: effect.DelayTicks, fault: out var fault, tick: firing.Tick);

        if (fault != ExpressionFault.None) {
            RefuseSource(effect: effect, fault: fault, firing: in firing);
            return Step.Faulted;
        }
        if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: StateWriteKind.Set, time: m_host.Time, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        moved = true;
        return Step.Done;
    }
    private Step FireInstanceFieldVectorWrite(InstanceFieldVectorWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        ReadOnlySpan<sbyte> source;

        if (effect.Source.Vector is { } vector) {
            var naming = vector.ReadSpan(reader: m_host, span: out source);

            if (naming != FaultCase.Finite) {
                ReportRefusal(detail: ((naming == FaultCase.Faulted) ? RuleReads.KeyFaultReason : "the vector source is absent"), effect: effect.Describe, refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
                return ((naming == FaultCase.Faulted)
                    ? Step.Faulted
                    : Step.Refused);
            }
        } else if ((effect.Source.Pool is { } pool) && (((uint)effect.Source.BindingSlot) < ((uint)bindings.Length)) && (bindings[effect.Source.BindingSlot].PoolOrdinal == pool.Ordinal) && m_host.Arena.TryReadVector(handle: bindings[effect.Source.BindingSlot], fieldOrdinal: effect.Source.Field.Ordinal, components: out source)) {
        } else {
            ReportRefusal(detail: "the bound vector source is absent or stale", effect: effect.Describe, refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (!m_host.Arena.TryWriteVector(handle: bindings[effect.BindingSlot], fieldOrdinal: effect.Field.Ordinal, components: source, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        moved = true;
        return Step.Done;
    }
    private Step FireArm(IRuleEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        var sources = effect.Sources.Length;
        var armed = firing;

        if (sources > 0) {
            var start = ReserveValues(count: sources);

            try {
                var read = ReadSources(
                    arm: effect,
                    firing: in firing,
                    start: start
                );

                if (read != Step.Done) {
                    return read;
                }

                armed = (firing with {
                    Values = new EffectValues(
                        count: sources,
                        start: start,
                        values: m_armOutcomes
                    ),
                });

                return TryFireArm(
                    arm: effect,
                    firing: in armed,
                    moved: out moved
                );
            } finally {
                m_armOutcomeCount = start;
            }
        }

        return TryFireArm(
            arm: effect,
            firing: in armed,
            moved: out moved
        );
    }
    private Step TryFireArm(IRuleEffect arm, in EffectFiring firing, out bool moved) {
        var effect = arm;

        if (effect.TryFire(
            firing: in firing,
            host: m_host,
            refusal: out var refusal
        )) {
            moved = effect.SubmitsMutation;

            return Step.Done;
        }

        moved = false;
        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return Step.Refused;
    }
    // The condition reads what the firing has already written into its open scope, exactly as any other read would.
    // A faulted condition runs neither branch and is already reported; propagate its refusal to the scope.
    private Step FireIf(IfEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        var verdict = Decide(
            gate: effect.Condition,
            ruleName: firing.RuleName
        );
        var open = (verdict is Verdict.Open);

        if (verdict is Verdict.Faulted) {
            if (m_traceEntry is not null) {
                m_traceEffectValue = "condition failed";
            }

            return Step.Faulted;
        }

        var branch = (open
            ? effect.Then
            : effect.Else
        );
        var fired = FireSequence(
            applied: ref moved,
            effects: branch,
            firing: in firing,
            strict: strict
        );

        // Set after firing the branch: a nested write's own value narration shares this one-shot slot. Only an
        // armed trace clears the slot, so only an armed trace fills it.
        if (m_traceEntry is not null) {
            m_traceEffectValue = (open
                ? "then"
                : ((effect.Else.Length > 0)
                    ? "else"
                    : "neither"
            ));
        }

        return fired;
    }
    // A savepoint is a nested scope inside the firing. A refused step rewinds it, so earlier siblings survive;
    // onFailure then runs in the firing's own scope and later siblings continue. With no onFailure the refusal
    // propagates, since the firing already rewinds as one. A step that meets a fault in the body refuses, which is the
    // savepoint's refusal and which the savepoint answers: onFailure runs and the firing can commit. A fault in
    // onFailure is the firing's.
    private Step FireSavepoint(TransactionEffect effect, in EffectFiring firing, out bool moved) {
        var arena = m_host.Arena;
        var queued = m_deferred.Count;
        var mark = arena.BeginScope();

        moved = false;

        Step held;

        try {
            held = FireSequence(
                applied: ref moved,
                effects: effect.Effects,
                firing: in firing,
                strict: true
            );
        } catch {
            // Scopes close innermost first, so the savepoint rewinds its own before the firing rewinds around it.
            arena.Rewind(mark: mark);
            DiscardQueued(from: queued);

            throw;
        }

        if (held == Step.Done) {
            arena.Commit(mark: mark);

            return Step.Done;
        }

        arena.Rewind(mark: mark);
        DiscardQueued(from: queued);
        moved = false;

        if (effect.OnFailure.Length == 0) {
            return held;
        }

        return FireSequence(
            applied: ref moved,
            effects: effect.OnFailure,
            firing: in firing,
            strict: false
        );
    }
    // A push resolves its value the way a write does, then lands as one ring push so the cursor and the slot move
    // together.
    private Step FirePush(PushStateEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        if (!TryReadValue(
            effect: effect,
            firing: in firing,
            kind: KindOf(rowOrdinal: effect.RowOrdinal),
            raw: out var raw,
            source: effect.Source,
            step: out var read
        )) {
            return read;
        }

        return Apply(
            effect: effect,
            firing: in firing,
            moved: out moved,
            mutation: Mutation.Push(
                rowOrdinal: effect.RowOrdinal,
                value: raw
            )
        );
    }
    // Under a savepoint a remove of an absent cell is submitted and refused by the door rather than skipped, so a
    // step that cannot do its work rolls its savepoint back.
    private Step FireRemove(RemoveStateCellEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        var resolved = RuleReads.ResolveKey(
            keyFrom: effect.KeyFrom,
            literal: effect.Key,
            reader: m_host
        );

        // A faulted key is never the no-op an absent cell is: the firing refuses whole, strict or not.
        if (resolved.Case == FaultCase.Faulted) {
            ReportRefusal(detail: RuleReads.KeyFaultReason, effect: effect.Describe,
                refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Faulted;
        }

        resolved.TryNamed(value: out var key);
        if (!key.IsValid) {
            if (!strict) {
                return Step.Done;
            }
            ReportRefusal(detail: "the remove address names no held cell", effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }
        if (
            !strict &&
            !m_host.Arena.TryCellSlot(
            key: key,
            rowOrdinal: effect.RowOrdinal,
            slot: out _
        )
        ) {
            return Step.Done;
        }

        return Apply(
            effect: effect,
            firing: in firing,
            moved: out moved,
            mutation: Mutation.Remove(
                key: key,
                rowOrdinal: effect.RowOrdinal
            )
        );
    }
    // The transform's rows, keys, cells and values were resolved once at compile time; a firing substitutes only
    // the parts a rule resolves fresh, and it substitutes them as a value rather than by rebuilding the record.
    private Step FireTransform(TransformStateEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        var fromResolved = RowOrdinal(row: effect.FromRow);
        var toResolved = RowOrdinal(row: effect.ToRow);
        var keyResolved = ((effect.KeyRef is { } indirection)
            ? RuleReads.ResolveReference(
                reader: m_host,
                reference: in indirection
            )
            : Resolved<CellKey>.Nothing);

        if (
            (fromResolved.Case == FaultCase.Faulted) ||
            (toResolved.Case == FaultCase.Faulted) ||
            (keyResolved.Case == FaultCase.Faulted)
        ) {
            ReportRefusal(detail: RuleReads.KeyFaultReason, effect: effect.Describe,
                refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Faulted;
        }

        // A live row that selected none substitutes no row, and a key that named nothing the invalid key.
        var fromRowOrdinal = (fromResolved.TryNamed(value: out var fromNamed) ? fromNamed : -1);
        var toRowOrdinal = (toResolved.TryNamed(value: out var toNamed) ? toNamed : -1);

        keyResolved.TryNamed(value: out var key);

        var binding = new ArenaTransformBinding(
            bindsKey: (effect.KeyRef is not null),
            fromRowOrdinal: fromRowOrdinal,
            key: key,
            toRowOrdinal: toRowOrdinal,
            bindsInstance: ((effect.Arena is ArenaTransform.PushRay ray) && (((uint)ray.OriginBindingSlot) < ((uint)m_host.InstanceBindings.Length))),
            instance: (((effect.Arena is ArenaTransform.PushRay pushRay) && (((uint)pushRay.OriginBindingSlot) < ((uint)m_host.InstanceBindings.Length))) ? m_host.InstanceBindings[pushRay.OriginBindingSlot] : default)
        );

        return ApplyTransform(
            binding: in binding,
            effect: effect,
            firing: in firing,
            moved: out moved,
            transform: effect.Arena
        );
    }
    // A vector transform's own terms carry their own live keys, so its resolved form is built per firing rather
    // than once at compile time.
    private Step FireVector(VectorEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        if (!ArenaTransforms.TryVectorTransform(
            catalog: m_host.Catalog,
            effect: effect,
            reader: m_host,
            reason: out var reason,
            transform: out var transform
        )) {
            ReportRefusal(
                detail: reason,
                effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return Step.Refused;
        }

        return ApplyTransform(
            binding: ArenaTransformBinding.None,
            effect: effect,
            firing: in firing,
            moved: out moved,
            transform: transform!
        );
    }
    private Step ApplyTransform(IRuleEffect effect, ArenaTransform transform, in ArenaTransformBinding binding, in EffectFiring firing, out bool moved) {
        if (m_host is not IArenaTransformHost host) {
            moved = false;
            ReportRefusal(
                detail: "this host applies no state transform",
                effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return Step.Refused;
        }
        if (host.TryTransform(
            binding: in binding,
            moved: out moved,
            refusal: out var refusal,
            transform: transform!
        )) {
            return Step.Done;
        }

        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return Step.Refused;
    }
    // A live row's selection, -1 when there is none to substitute (no live row, or an index selecting none).
    private Resolved<int> RowOrdinal(LiveRow? row) => ((row is { } live)
        ? live.Resolve(reader: m_host)
        : Resolved<int>.Nothing);
    private Step FireWrite(IRuleEffect effect, IStateWriteEffect write, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        var ordinal = write.RowOrdinal;

        // A live destination picks its row per firing. An index that selects none is an absence, not a no-op: the
        // author wrote a value and it would go nowhere, so the firing rewinds exactly as an absent source does. The
        // negative-ordinal skip below is the separate, compile-time-absent case.
        if (write.RowFrom is { } selection) {
            var selected = selection.Resolve(reader: m_host);

            if (!selected.TryNamed(value: out ordinal)) {
                ReportRefusal(
                    detail: ((selected.Case == FaultCase.Faulted)
                        ? $"{RuleReads.KeyFaultReason}; '{selection.Spelling}' selects no row"
                        : $"{RuleEvaluation.DescribeFault(fault: ExpressionFault.Absent)}; '{selection.Spelling}' selects no row"),
                    effect: effect.Describe,
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: firing.RuleName,
                    tick: firing.Tick
                );

                return ((selected.Case == FaultCase.Faulted)
                    ? Step.Faulted
                    : Step.Refused);
            }
        }
        if (ordinal < 0) {
            return Step.Done;
        }

        var written = RuleReads.ResolveKeyForWrite(
            keyFrom: write.KeyFrom,
            literal: write.Key,
            reader: m_host
        );

        if (!written.TryResolved(key: out var key)) {
            ReportRefusal(
                detail: written.Reason,
                effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return ((written.Case == WriteKeyCase.Faulted)
                ? Step.Faulted
                : Step.Refused);
        }

        if (!key.IsValid) {
            if (!strict) {
                return Step.Done;
            }
            ReportRefusal(detail: "the write address names no cell", effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return Step.Refused;
        }

        var kind = KindOf(rowOrdinal: ordinal);

        if (kind == CellKind.Text) {
            return FireWriteText(
                effect: effect,
                firing: in firing,
                key: key,
                moved: out moved,
                rowOrdinal: ordinal
            );
        }

        long raw;

        switch (effect) {
            case ScheduleStateEffect schedule: {
                    raw = ScheduleDueTick(
                        delayTicks: schedule.DelayTicks,
                        fault: out var fault,
                        tick: firing.Tick
                    );
                    if (fault != ExpressionFault.None) {
                        RefuseSource(
                            effect: effect,
                            fault: fault,
                            firing: in firing
                        );

                        return Step.Faulted;
                    }

                    break;
                }
            default: {
                    var previousWriteKey = m_host.Scratch.WriteKey;

                    m_host.Scratch.WriteKey = key;
                    try {
                        if (!TryReadValue(
                            effect: effect,
                            firing: in firing,
                            kind: kind,
                            raw: out raw,
                            source: ((IValueSourcedEffect)write).Source,
                            step: out var read
                        )) {
                            return read;
                        }
                    } finally {
                        m_host.Scratch.WriteKey = previousWriteKey;
                    }

                    break;
                }
        }

        if (m_traceEntry is not null) {
            m_traceEffectValue = RuleEvaluation.DescribeFact(
                isForever: false,
                kind: kind,
                value: raw
            );
        }

        return Apply(
            effect: effect,
            firing: in firing,
            moved: out moved,
            mutation: Mutation.Written(
                key: key,
                operand: raw,
                rowOrdinal: ordinal,
                write: write.Write
            )
        );
    }
    private Step FireWriteText(IRuleEffect effect, int rowOrdinal, CellKey key, in EffectFiring firing, out bool moved) {
        moved = false;

        // A schedule row is refused at compile time unless it is kind=Int, so a text row here can only carry a plain
        // write.
        var write = ((WriteEffect)effect);
        var text = write.Text;

        if ((text is null) && !TryReadText(effect: effect, firing: in firing, source: write.Source.Operand, step: out var textStep, text: out text)) {
            return textStep;
        }

        return Apply(
            effect: effect,
            firing: in firing,
            moved: out moved,
            mutation: Mutation.WrittenText(
                key: key,
                rowOrdinal: rowOrdinal,
                text: text
            )
        );
    }
    private Step Apply(IRuleEffect effect, in Mutation mutation, in EffectFiring firing, out bool moved) {
        moved = m_host.Apply(
            mutation: in mutation,
            refusal: out var refusal
        );

        if (!refusal.IsRefused) {
            return Step.Done;
        }

        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return Step.Refused;
    }
    private CellKind KindOf(int rowOrdinal) {
        var layout = m_host.Arena.Layout;

        return ((((uint)rowOrdinal) < ((uint)layout.RowCount))
            ? layout[rowOrdinal].Kind
            : CellKind.Int
        );
    }
    // A Text source that answers no cell skips its effect, as an absent text always has (step Done); one whose address
    // faulted faults the firing, reported here.
    private bool TryReadText(IRuleEffect effect, IRuleOperand? source, in EffectFiring firing, out string text, out Step step) {
        var resolved = RuleEvaluation.ReadText(
            reader: m_host,
            source: source
        );
        var named = resolved.TryNamed(value: out var read);

        text = (read ?? string.Empty);
        step = ((resolved.Case == FaultCase.Faulted)
            ? Step.Faulted
            : Step.Done);
        if (step == Step.Faulted) {
            ReportRefusal(detail: RuleReads.KeyFaultReason, effect: effect.Describe,
                refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
        }

        return named;
    }
    private void RefuseSource(IRuleEffect effect, in EffectFiring firing, ExpressionFault fault) => ReportRefusal(
        detail: RuleEvaluation.DescribeFault(fault: fault),
        effect: effect.Describe,
        refusal: RuleEffectRefusal.Arithmetic,
        ruleName: firing.RuleName,
        tick: firing.Tick
    );
    // One value read per effect. A forever fact is nothing to copy and skips the effect silently (step Done); an
    // absence is a refusal naming the operations that answer it, so the firing rewinds rather than half-landing; a
    // fault faults the firing.
    private bool TryReadValue(IRuleEffect effect, in CompiledValueSource source, CellKind kind, in EffectFiring firing, out long raw, out Step step) {
        raw = 0L;
        step = Step.Done;

        var frame = new EvaluationFrame(reader: m_host);
        var fact = RuleEvaluation.ReadSource(
            frame: ref frame,
            kind: kind,
            source: in source
        );

        // A program that left no number refuses with its own fault; an operand's absence or fault is worded below.
        if (source.IsExpression && (fact.Outcome.Case is FaultCase.Absent or FaultCase.Faulted)) {
            step = ((fact.Outcome.Case == FaultCase.Faulted)
                ? Step.Faulted
                : Step.Refused);
            RefuseSource(
                effect: effect,
                fault: ((fact.Outcome.Case == FaultCase.Absent)
                    ? ExpressionFault.Absent
                    : fact.Outcome.Fault),
                firing: in firing
            );

            return false;
        }
        if (fact.Outcome.Case == FaultCase.Forever) {
            return false;
        }
        if (fact.Outcome.Case is FaultCase.Absent or FaultCase.Faulted) {
            step = ((fact.Outcome.Case == FaultCase.Faulted)
                ? Step.Faulted
                : Step.Refused);
            ReportRefusal(
                detail: $"{RuleEvaluation.DescribeFault(fault: ((fact.Outcome.Case == FaultCase.Faulted) ? ExpressionFault.Domain : ExpressionFault.Absent))}; the effect did not fire",
                effect: effect.Describe,
                refusal: RuleEffectRefusal.Arithmetic,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return false;
        }

        fact.Outcome.TryFinite(raw: out var finite);

        raw = (source.IsOperand
            ? fact.ToRaw(kind: kind)
            : finite
        );

        return true;
    }
}
