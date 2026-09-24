using System.Globalization;

namespace Puck.State.Rules;

public sealed partial class RuleEvaluator {
    private readonly List<IRuleEffect> m_deferred = [];

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
    /// <returns><see cref="RuleOutcome.Fired"/> when the scope committed; otherwise <see cref="RuleOutcome.Refused"/>.</returns>
    /// <remarks>A host evaluating a rule kind of its own — a decision's option or its no-choice arm — fires through
    /// here so its effects are one scope, exactly as a rule's own are.</remarks>
    public RuleOutcome FireEffects(IRuleEffect[] effects, string ruleName, ulong tick, ulong stepTicks, out bool applied) {
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

        var arena = m_host.Arena;
        var firing = new EffectFiring(
            EngineTick: m_host.EngineTick,
            RuleName: ruleName,
            StepTicks: stepTicks,
            Tick: tick
        );
        var queued = m_deferred.Count;
        var mark = arena.BeginScope();

        applied = false;

        bool proposed;

        try {
            proposed = Propose(
                applied: ref applied,
                effects: effects,
                firing: in firing,
                queued: queued,
                ruleName: ruleName
            );
        } catch {
            arena.Rewind(mark: mark);
            DiscardQueued(from: queued);

            throw;
        }

        if (!proposed) {
            arena.Rewind(mark: mark);
            DiscardQueued(from: queued);
            applied = false;

            return RuleOutcome.Refused;
        }

        arena.Commit(mark: mark);

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
                if (arm.TryFire(
                    firing: in firing,
                    host: m_host,
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
        }

        return RuleOutcome.Fired;
    }

    // Everything a firing does before its commit: the reversible effects, then each queued arm's preflight against
    // the state they propose, then the host's preparation of the transactional arms as one unit. False means
    // refused, already reported.
    private bool Propose(IRuleEffect[] effects, string ruleName, in EffectFiring firing, int queued, ref bool applied) {
        if (!FireSequence(
            applied: ref applied,
            effects: effects,
            firing: in firing,
            strict: false
        )) {
            return false;
        }

        var preflight = (firing with { Preflight = true });

        if (m_deferred.Count > queued) {
            m_host.Preflighting();
        }

        for (var index = queued; (index < m_deferred.Count); index++) {
            var arm = m_deferred[index];

            if (!arm.TryFire(
                firing: in preflight,
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

                return false;
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

            return false;
        }

        return true;
    }
    private bool QueuedTransactional(int from) {
        for (var index = from; (index < m_deferred.Count); index++) {
            if ((m_deferred[index].Needs & EffectNeeds.Transactional) != EffectNeeds.None) {
                return true;
            }
        }

        return false;
    }
    private void DiscardQueued(int from) => m_deferred.RemoveRange(
        count: (m_deferred.Count - from),
        index: from
    );
    // Fires one effect sequence in order, returning false on the first refusal. An arm that cannot be rewound is
    // queued rather than fired, at any depth.
    private bool FireSequence(IRuleEffect[] effects, in EffectFiring firing, bool strict, ref bool applied) {
        var trace = m_traceEntry;

        for (var index = 0; (index < effects.Length); index++) {
            var effect = effects[index];

            if ((effect.Needs & EffectNeeds.Irreversible) != EffectNeeds.None) {
                m_deferred.Add(item: effect);
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
            if (!fired) {
                return false;
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

                return false;
            }
        }

        return true;
    }
    // Returns false only when the effect refused, which rewinds the enclosing scope; an effect that fired without
    // moving the arena, or that could not move its destination, returns true with `moved` false.
    private bool FireOne(IRuleEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        switch (effect) {
            case ClaimEffect claim:
                return FireClaim(effect: claim, firing: in firing, moved: out moved);
            case ClaimPairEffect pair:
                return FireClaimPair(effect: pair, firing: in firing, moved: out moved);
            case ReleaseEffect release:
                return FireRelease(effect: release, firing: in firing, moved: out moved);
            case ForEachPoolEffect each:
                return FireForEachPool(effect: each, firing: in firing, moved: out moved);
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
    private bool FireClaim(ClaimEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if (((uint)effect.BindingSlot) >= ((uint)bindings.Length)) {
            ReportRefusal(detail: "this host exposes no pool-instance binding register", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        if (!m_host.Arena.TryClaim(poolOrdinal: effect.Pool.Ordinal, time: m_host.Time, handle: out var handle, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        var previous = bindings[effect.BindingSlot];

        bindings[effect.BindingSlot] = handle;
        try {
            var bodyMoved = false;

            if (!FireSequence(effects: effect.Effects, firing: in firing, strict: false, applied: ref bodyMoved)) {
                return false;
            }

            moved = true;
            return true;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private bool FireClaimPair(ClaimPairEffect effect, in EffectFiring firing, out bool moved) {
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
            return false;
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
            return false;
        }

        var previous = bindings[effect.BindingSlot];

        bindings[effect.BindingSlot] = handle;
        try {
            var bodyMoved = false;

            if (!FireSequence(effects: effect.Effects, firing: in firing, strict: false, applied: ref bodyMoved)) {
                return false;
            }
            moved = true;
            return true;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private bool FireRelease(ReleaseEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the released pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        if (!m_host.Arena.TryRelease(handle: bindings[effect.BindingSlot], reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        moved = true;
        return true;
    }
    private bool FireForEachPool(ForEachPoolEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if (((uint)effect.BindingSlot) >= ((uint)bindings.Length)) {
            ReportRefusal(detail: "this host exposes no pool-instance binding register", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        var snapshot = PoolHandles(pool: effect.Pool, bindingSlot: effect.BindingSlot);
        var previous = bindings[effect.BindingSlot];

        try {
            foreach (var handle in snapshot) {
                if (!m_host.Arena.TryResolve(handle: handle, position: out _)) {
                    continue;
                }

                bindings[effect.BindingSlot] = handle;
                if (!FireSequence(effects: effect.Effects, firing: in firing, strict: false, applied: ref moved)) {
                    return false;
                }
            }
            return true;
        } finally {
            bindings[effect.BindingSlot] = previous;
        }
    }
    private bool FireInstanceFieldWrite(InstanceFieldWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        var handle = bindings[effect.BindingSlot];

        if (effect.Field.Kind == CellKind.Text) {
            var text = effect.Text;

            if ((text is null) && !TryReadText(source: effect.Source.Operand, text: out text)) {
                return true;
            }
            if (!m_host.Arena.TryWrite(handle: handle, fieldOrdinal: effect.Field.Ordinal, value: CellValue.Text(value: text), reason: out var textReason)) {
                ReportRefusal(detail: textReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
        } else {
            if (!TryReadValue(effect: effect, source: effect.Source, kind: effect.Field.Kind, firing: in firing, raw: out var raw, refused: out var refused)) {
                return !refused;
            }

            if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: effect.Write, time: m_host.Time, reason: out var numericReason)) {
                ReportRefusal(detail: numericReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
        }
        moved = true;
        return true;
    }
    private bool FireStaticInstanceFieldWrite(StaticInstanceFieldWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        if (!m_host.Arena.TryResolvePoolSlot(poolOrdinal: effect.Pool.Ordinal, slot: effect.Handle.Slot, handle: out var handle)) {
            ReportRefusal(detail: $"State pool '{effect.Pool.Name.Value}' slot {effect.Handle.Slot} is absent.", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        if (effect.Field.Kind == CellKind.Text) {
            var text = effect.Text;

            if ((text is null) && !TryReadText(source: effect.Source.Operand, text: out text)) {
                return true;
            }
            if (!m_host.Arena.TryWrite(handle: handle, fieldOrdinal: effect.Field.Ordinal, value: CellValue.Text(value: text), reason: out var textReason)) {
                ReportRefusal(detail: textReason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
        } else {
            if (!TryReadValue(effect: effect, source: effect.Source, kind: effect.Field.Kind, firing: in firing, raw: out var raw, refused: out var refused)) {
                return !refused;
            }
            if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: effect.Write, time: m_host.Time, reason: out var reason)) {
                ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
        }
        moved = true;
        return true;
    }
    private bool FireInstanceFieldSchedule(InstanceFieldScheduleEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        StateInstanceHandle handle;

        if (effect.BindingSlot >= 0) {
            var bindings = m_host.InstanceBindings;

            if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
                ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
            handle = bindings[effect.BindingSlot];
        } else if (!m_host.Arena.TryResolvePoolSlot(poolOrdinal: effect.Pool.Ordinal, slot: effect.StaticSlot, handle: out handle)) {
            ReportRefusal(detail: $"State pool '{effect.Pool.Name.Value}' slot {effect.StaticSlot} is absent.", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        var raw = ScheduleDueTick(delayTicks: effect.DelayTicks, fault: out var fault, tick: firing.Tick);

        if (fault != ExpressionFault.None) {
            RefuseSource(effect: effect, fault: fault, firing: in firing);
            return false;
        }
        if (!m_host.Arena.TryWriteLive(handle: handle, fieldOrdinal: effect.Field.Ordinal, operand: raw, write: StateWriteKind.Set, time: m_host.Time, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        moved = true;
        return true;
    }
    private bool FireInstanceFieldVectorWrite(InstanceFieldVectorWriteEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;
        var bindings = m_host.InstanceBindings;

        if ((((uint)effect.BindingSlot) >= ((uint)bindings.Length)) || (bindings[effect.BindingSlot].PoolOrdinal != effect.Pool.Ordinal)) {
            ReportRefusal(detail: "the target pool binding is absent", effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        ReadOnlySpan<sbyte> source;

        if (effect.Source.Vector is { } vector) {
            if (!vector.TryReadSpan(reader: m_host, span: out source)) {
                ReportRefusal(detail: "the vector source is absent", effect: effect.Describe, refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
                return false;
            }
        } else if ((effect.Source.Pool is { } pool) && (((uint)effect.Source.BindingSlot) < ((uint)bindings.Length)) && (bindings[effect.Source.BindingSlot].PoolOrdinal == pool.Ordinal) && m_host.Arena.TryReadVector(handle: bindings[effect.Source.BindingSlot], fieldOrdinal: effect.Source.Field.Ordinal, components: out source)) {
        } else {
            ReportRefusal(detail: "the bound vector source is absent or stale", effect: effect.Describe, refusal: RuleEffectRefusal.Arithmetic, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        if (!m_host.Arena.TryWriteVector(handle: bindings[effect.BindingSlot], fieldOrdinal: effect.Field.Ordinal, components: source, reason: out var reason)) {
            ReportRefusal(detail: reason, effect: effect.Describe, refusal: RuleEffectRefusal.MutationRejected, ruleName: firing.RuleName, tick: firing.Tick);
            return false;
        }
        moved = true;
        return true;
    }
    private bool FireArm(IRuleEffect effect, in EffectFiring firing, out bool moved) {
        if (effect.TryFire(
            firing: in firing,
            host: m_host,
            refusal: out var refusal
        )) {
            moved = effect.SubmitsMutation;

            return true;
        }

        moved = false;
        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return false;
    }
    // The condition reads what the firing has already written into its open scope, exactly as any other read would.
    // A faulted condition runs neither branch and is already reported; it is not itself a refusal of the firing.
    private bool FireIf(IfEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        var open = GateOpen(
            faulted: out var faulted,
            gate: effect.Condition,
            ruleName: firing.RuleName
        );

        if (faulted) {
            if (m_traceEntry is not null) {
                m_traceEffectValue = "condition failed";
            }

            return true;
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
    // propagates, since the firing already rewinds as one.
    private bool FireSavepoint(TransactionEffect effect, in EffectFiring firing, out bool moved) {
        var arena = m_host.Arena;
        var queued = m_deferred.Count;
        var mark = arena.BeginScope();

        moved = false;

        bool held;

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

        if (held) {
            arena.Commit(mark: mark);

            return true;
        }

        arena.Rewind(mark: mark);
        DiscardQueued(from: queued);
        moved = false;

        if (effect.OnFailure.Length == 0) {
            return false;
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
    private bool FirePush(PushStateEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        if (!TryReadValue(
            effect: effect,
            firing: in firing,
            kind: KindOf(rowOrdinal: effect.RowOrdinal),
            raw: out var raw,
            refused: out var refused,
            source: effect.Source
        )) {
            return !refused;
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
    private bool FireRemove(RemoveStateCellEffect effect, in EffectFiring firing, bool strict, out bool moved) {
        moved = false;

        var key = RuleReads.ResolveKey(
            keyFrom: effect.KeyFrom,
            literal: effect.Key,
            reader: m_host
        );

        if (!key.IsValid) {
            return true;
        }
        if (
            !strict &&
            !m_host.Arena.TryCellSlot(
            key: key,
            rowOrdinal: effect.RowOrdinal,
            slot: out _
        )
        ) {
            return true;
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
    private bool FireTransform(TransformStateEffect effect, in EffectFiring firing, out bool moved) {
        moved = false;

        var binding = new ArenaTransformBinding(
            bindsKey: (effect.KeyRef is not null),
            fromRowOrdinal: RowOrdinal(row: effect.FromRow),
            key: ((effect.KeyRef is { } indirection)
            ? RuleReads.ResolveReference(
                reader: m_host,
                reference: in indirection
            )
            : default),
            toRowOrdinal: RowOrdinal(row: effect.ToRow),
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
    private bool FireVector(VectorEffect effect, in EffectFiring firing, out bool moved) {
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

            return false;
        }

        return ApplyTransform(
            binding: ArenaTransformBinding.None,
            effect: effect,
            firing: in firing,
            moved: out moved,
            transform: transform!
        );
    }
    private bool ApplyTransform(IRuleEffect effect, ArenaTransform transform, in ArenaTransformBinding binding, in EffectFiring firing, out bool moved) {
        if (m_host is not IArenaTransformHost host) {
            moved = false;
            ReportRefusal(
                detail: "this host applies no state transform",
                effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return false;
        }
        if (host.TryTransform(
            binding: in binding,
            moved: out moved,
            refusal: out var refusal,
            transform: transform!
        )) {
            return true;
        }

        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return false;
    }
    private int RowOrdinal(LiveRow? row) => (((row is { } live) && live.TryResolve(
        reader: m_host,
        rowOrdinal: out var ordinal
    ))
        ? ordinal
        : -1
    );
    private bool FireWrite(IRuleEffect effect, IStateWriteEffect write, in EffectFiring firing, out bool moved) {
        moved = false;

        var ordinal = write.RowOrdinal;

        // A live destination picks its row per firing. An index that selects none is an absence, not a no-op: the
        // author wrote a value and it would go nowhere, so the firing rewinds exactly as an absent source does. The
        // negative-ordinal skip below is the separate, compile-time-absent case.
        if (write.RowFrom is { } selection) {
            if (!selection.TryResolve(
                reader: m_host,
                rowOrdinal: out ordinal
            )) {
                ReportRefusal(
                    detail: $"{RuleEvaluation.DescribeFault(fault: ExpressionFault.Absent)}; '{selection.Spelling}' selects no row",
                    effect: effect.Describe,
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: firing.RuleName,
                    tick: firing.Tick
                );

                return false;
            }
        }
        if (ordinal < 0) {
            return true;
        }

        if (!RuleReads.TryResolveKeyForWrite(
            keyFrom: write.KeyFrom,
            literal: write.Key,
            key: out var key,
            reader: m_host,
            reason: out var keyReason
        )) {
            ReportRefusal(
                detail: keyReason,
                effect: effect.Describe,
                refusal: RuleEffectRefusal.MutationRejected,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return false;
        }

        if (!key.IsValid) {
            return true;
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

                        return false;
                    }

                    break;
                }
            default: {
                    if (!TryReadValue(
                        effect: effect,
                        firing: in firing,
                        kind: kind,
                        raw: out raw,
                        refused: out var refused,
                        source: ((IValueSourcedEffect)write).Source
                    )) {
                        return !refused;
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
    private bool FireWriteText(IRuleEffect effect, int rowOrdinal, CellKey key, in EffectFiring firing, out bool moved) {
        moved = false;

        // A schedule row is refused at compile time unless it is kind=Int, so a text row here can only carry a plain
        // write.
        var write = ((WriteEffect)effect);
        var text = write.Text;

        if ((text is null) && !TryReadText(source: write.Source.Operand, text: out text)) {
            return true;
        }

        if (text is null) {
            return true;
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
    private bool TryReadText(IRuleOperand? source, out string text) {
        text = string.Empty;
        CellValue carried;

        switch (source) {
            case StateCellOperand cell: {
                    var ordinal = cell.RowOrdinal;

                    if ((cell.RowFrom is { } live) && !live.TryResolve(reader: m_host, rowOrdinal: out ordinal)) {
                        return false;
                    }
                    var key = RuleReads.ResolveKey(keyFrom: cell.KeyFrom, literal: cell.Key, reader: m_host);

                    if (!key.IsValid || !m_host.Arena.TryRead(key: key, rowOrdinal: ordinal, value: out carried)) {
                        return false;
                    }
                    break;
                }
            case InstanceFieldOperand field: {
                    var bindings = m_host.InstanceBindings;

                    if ((((uint)field.BindingSlot) >= ((uint)bindings.Length)) || (bindings[field.BindingSlot].PoolOrdinal != field.Pool.Ordinal) || !m_host.Arena.TryReadLive(handle: bindings[field.BindingSlot], fieldOrdinal: field.Field.Ordinal, time: m_host.Time, value: out carried)) {
                        return false;
                    }
                    break;
                }
            case StaticInstanceFieldOperand field: {
                    if (!m_host.Arena.TryResolvePoolSlot(poolOrdinal: field.Pool.Ordinal, slot: field.Handle.Slot, handle: out var handle) ||
                        !m_host.Arena.TryReadLive(handle: handle, fieldOrdinal: field.Field.Ordinal, time: m_host.Time, value: out carried)) {
                        return false;
                    }
                    break;
                }
            default:
                return false;
        }

        if (carried.Kind != CellKind.Text) {
            return false;
        }
        text = carried.AsText;
        return true;
    }
    private bool Apply(IRuleEffect effect, in Mutation mutation, in EffectFiring firing, out bool moved) {
        moved = m_host.Apply(
            mutation: in mutation,
            refusal: out var refusal
        );

        if (!refusal.IsRefused) {
            return true;
        }

        ReportRefusal(
            effect: effect.Describe,
            fallback: RuleEffectRefusal.MutationRejected,
            refusal: in refusal,
            ruleName: firing.RuleName,
            tick: firing.Tick
        );

        return false;
    }
    private CellKind KindOf(int rowOrdinal) {
        var layout = m_host.Arena.Layout;

        return ((((uint)rowOrdinal) < ((uint)layout.RowCount))
            ? layout[rowOrdinal].Kind
            : CellKind.Int
        );
    }
    private void RefuseSource(IRuleEffect effect, in EffectFiring firing, ExpressionFault fault) => ReportRefusal(
        detail: RuleEvaluation.DescribeFault(fault: fault),
        effect: effect.Describe,
        refusal: RuleEffectRefusal.Arithmetic,
        ruleName: firing.RuleName,
        tick: firing.Tick
    );
    // One value read per effect. A forever fact is nothing to copy and skips the effect silently; an absence is a
    // refusal naming the operations that answer it, so the firing rewinds rather than half-landing.
    private bool TryReadValue(IRuleEffect effect, in CompiledValueSource source, CellKind kind, in EffectFiring firing, out long raw, out bool refused) {
        raw = 0L;
        refused = false;

        if (!RuleEvaluation.TryReadSource(
            fact: out var fact,
            fault: out var fault,
            kind: kind,
            reader: m_host,
            source: in source
        )) {
            refused = true;
            RefuseSource(
                effect: effect,
                fault: fault,
                firing: in firing
            );

            return false;
        }
        if (fact.IsForever) {
            return false;
        }
        if (fact.IsAbsent) {
            refused = true;
            ReportRefusal(
                detail: $"{RuleEvaluation.DescribeFault(fault: ExpressionFault.Absent)}; the effect did not fire",
                effect: effect.Describe,
                refusal: RuleEffectRefusal.Arithmetic,
                ruleName: firing.RuleName,
                tick: firing.Tick
            );

            return false;
        }

        raw = (source.IsOperand
            ? fact.ToRaw(kind: kind)
            : fact.Value
        );

        return true;
    }
}
