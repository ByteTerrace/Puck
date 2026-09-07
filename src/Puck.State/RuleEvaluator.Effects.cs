namespace Puck.State;

public sealed partial class RuleEvaluator {
    private bool m_preflightRejected;

    /// <summary>Fires a rule's top-level effects in order. Every top-level effect is its own boundary: each performs
    /// its own refusal-suppressing preflight and either installs or refuses alone, so a later effect's refusal never
    /// rolls back an earlier sibling's write. The one atomic group is the explicit <c>transaction</c> effect, which
    /// preflights its whole branch as one candidate before any of it installs.</summary>
    /// <param name="effects">The compiled effects.</param>
    /// <param name="ruleName">The firing rule's name.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">How many ticks the step spans.</param>
    /// <returns><see langword="true"/> when any effect installed a mutation.</returns>
    public bool FireEffects(EffectFact[] effects, string ruleName, ulong tick, ulong stepTicks) {
        var applied = false;
        var trace = m_traceEntry;

        for (var index = 0; index < effects.Length; index++) {
            var effect = effects[index];
            var serial = m_refusalSerial;
            var fired = Fire(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: false, strict: false);

            applied |= fired;
            trace?.Effects.Add(item: DescribeTracedEffect(effect: effect, applied: fired, refused: (m_refusalSerial != serial)));
        }

        return applied;
    }

    // A write that cannot move the destination is skipped before submission — either the resolved value already
    // matches the cell, or the row's declared envelope pins the cell where it is: a level-triggered gate re-fires
    // every tick it holds, and without this a standing rule would append an identical journal entry forever, or draw
    // an identical refusal forever. A generate is never a no-op — it advances the generator's cursor by construction.
    // Under `strict` (a transaction step, or the private preflight of a top-level effect) a remove of an absent cell
    // is submitted and refused by the door rather than skipped.
    private bool Fire(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight, bool strict) {
        switch (effect) {
            case TransformStateEffect transformState:
                var transform = transformState.Transform;
                if (transformState.KeyRef is { } keyRef) {
                    transform = transform switch {
                        StateTransform.Transfer transfer => transfer with { Key = ResolveKey(key: transfer.Key, keyFrom: keyRef, tick: tick) },
                        StateTransform.ClearEnclosed enclosed => enclosed with { From = ResolveKey(key: enclosed.From, keyFrom: keyRef, tick: tick) },
                        StateTransform.WriteSet writeSet => writeSet with { SetKey = ResolveKey(key: writeSet.SetKey, keyFrom: keyRef, tick: tick) },
                        _ => transform,
                    };
                }
                // A live end resolves to its zone's name fresh every firing; an index selecting none resolves to the
                // authored spelling, which no row carries, so the door refuses the transfer by name.
                if (transform is StateTransform.Transfer liveEnds && ((transformState.FromZone is not null) || (transformState.ToZone is not null))) {
                    Tick = tick;
                    transform = liveEnds with {
                        From = (transformState.FromZone?.ResolveName(reader: m_host) ?? liveEnds.From),
                        To = (transformState.ToZone?.ResolveName(reader: m_host) ?? liveEnds.To),
                    };
                }
                return Apply(effect: effect, ruleName: ruleName, mutation: new StateMutation.Apply(Transform: transform), tick: tick, preflight: preflight);
            case TransactionEffect transaction:
                return FireTransaction(transaction: transaction, ruleName: ruleName, tick: tick, stepTicks: stepTicks);
            case PushStateEffect push:
                return FirePush(effect: push, ruleName: ruleName, tick: tick, preflight: preflight);
        }

        if (!effect.SubmitsMutation) {
            return Outcome(outcome: m_host.FireEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: preflight), preflight: preflight);
        }

        if (!preflight && !strict) {
            m_preflightRejected = false;
            m_host.BeginPreflight();
            try {
                _ = Fire(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: true, strict: true);
            } finally {
                m_host.EndPreflight();
            }
            if (m_preflightRejected) {
                m_preflightRejected = false;

                return false;
            }
        }

        if (effect is not IStateAddressedEffect addressed) {
            return Outcome(outcome: m_host.FireEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: preflight), preflight: preflight);
        }

        // A '$cell:' destination resolves its key fresh every firing, exactly as a gate operand's does.
        var destinationKey = ResolveKey(key: addressed.Key, keyFrom: addressed.KeyFrom, tick: tick);

        if (effect is RemoveStateCellEffect removeStateCell) {
            if (
                StateReader.TryRead(store: m_host.Store, rowName: removeStateCell.Row, key: destinationKey, tick: tick, row: out _, rawValue: out var existing, text: out var existingText) &&
                (existing is null) &&
                (existingText is null) &&
                !strict
            ) {
                return false;
            }

            return Apply(effect: effect, ruleName: ruleName, mutation: new StateMutation.RemoveCell(Row: removeStateCell.Row, Key: destinationKey), tick: tick, preflight: preflight);
        }

        if (effect is IStateWriteEffect write) {
            return FireWrite(effect: effect, write: write, destinationKey: destinationKey, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: preflight, strict: strict);
        }

        if (effect is GenerateEffect generate) {
            return Apply(effect: effect, ruleName: ruleName, mutation: new StateMutation.Generate(Row: generate.Row), tick: tick, preflight: preflight);
        }

        return Outcome(outcome: m_host.FireEffect(effect: effect, ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: preflight), preflight: preflight);
    }

    private bool FireWrite(EffectFact effect, IStateWriteEffect write, string destinationKey, string ruleName, ulong tick, ulong stepTicks, bool preflight, bool strict) {
        // The destination's current value through the same resolver the gate read: an absent cell reads as zero (an
        // Add mints it), an absent row is nothing to write. On an advancing row that is the live value, not the
        // stored base: a base is a fixed point of its own accumulation, so comparing against it would call a write
        // "no-op" whenever the base already happened to match — silently skipping the write, and with it the rebase
        // that is the only way a rule can reset an advancing row at all.
        if (!StateReader.TryRead(store: m_host.Store, rowName: write.Row, key: destinationKey, tick: tick, row: out var row, rawValue: out var destination, text: out var currentText)) {
            return false;
        }

        if (row.Kind == CellKind.Text) {
            // A schedule/countdown row is refused at compile time unless kind=Int, so a text row here can only be a
            // Write.
            var textWrite = (WriteEffect)write;
            var nextText = textWrite.Text;

            if ((nextText is null) && (textWrite.From is StateCellOperand source)) {
                if (!StateReader.TryRead(store: m_host.Store, rowName: source.Row, key: ResolveKey(key: source.Key, keyFrom: source.KeyFrom, tick: tick), tick: tick, row: out _, rawValue: out _, text: out nextText)) {
                    return false;
                }
            }

            if ((nextText is null) || string.Equals(a: currentText, b: nextText, comparisonType: StringComparison.Ordinal)) {
                return false;
            }

            return Apply(
                effect: effect,
                ruleName: ruleName,
                mutation: new StateMutation.UpsertCell(Row: write.Row, Key: destinationKey, Value: 0L, Write: StateWriteKind.Set, Text: nextText),
                tick: tick,
                preflight: preflight
            );
        }

        var current = (destination ?? 0L);

        // A cycling cell stores its phase and reads its rotation; a write moves the phase, so the value an add turns
        // from and the value the could-this-move test compares against is the stored phase, not the live rotation.
        if (
            CellName.TryParse(candidate: destinationKey, name: out var destinationCell, reason: out _) &&
            (StateRows.FindCell(cells: row.Cells, key: destinationCell) is { } storedCell) &&
            ((storedCell.Cycle is not null) || ((storedCell.Key == StateRow.SlotKey) && (row.Cycle is not null))) &&
            m_host.Store.TryStored(row: row, key: destinationCell, value: out var storedPhase, text: out _)
        ) {
            current = storedPhase;
        }

        var fault = ExpressionFault.None;
        long raw;

        if (write is CountdownEffect) {
            raw = -Math.Min(val1: current, val2: checked((long)stepTicks));
        } else if (write is ScheduleStateEffect schedule) {
            raw = ScheduleDueTick(tick: tick, delayTicks: schedule.DelayTicks, fault: out fault);
        } else if (!TryReadSource((WriteEffect)write, row.Kind, tick, out raw, out fault) && fault == ExpressionFault.None) {
            return false;
        }

        if (fault != ExpressionFault.None) {
            RefuseSource(effect, ruleName, tick, preflight, fault);

            return false;
        }

        if ((m_traceEntry is not null) && !strict) {
            m_traceEffectValue = RuleEvaluation.DescribeFact(value: raw, kind: row.Kind, isForever: false);
        }

        var next = ((write.Write == StateWriteKind.Add) ? unchecked(current + raw) : raw);

        // Submit only what could move the destination. Arithmetic identity is not the whole of that test: a cell
        // already sitting on a bound its own row declares cannot be pushed further past it, so a Level gate pointed
        // at a floored row would go on composing a candidate the validator refuses, once per tick, for the life of
        // the session. The projection decides whether to submit and never what is submitted: the mutation still
        // carries the rule's own unclamped operand, so a write that genuinely tries to cross a bound is still
        // submitted and still refused by name.
        if (row.ClampToEnvelope(value: next) == current) {
            return false;
        }

        return Apply(
            effect: effect,
            ruleName: ruleName,
            mutation: new StateMutation.UpsertCell(Row: write.Row, Key: destinationKey, Value: raw, Write: write.Write),
            tick: tick,
            preflight: preflight
        );
    }

    // pushState: the value is resolved the way a write's is, then lands as a Push transform so the ring's cursor and
    // slot move in one journaled mutation.
    private bool FirePush(PushStateEffect effect, string ruleName, ulong tick, bool preflight) {
        if ((m_host.Store.Find(name: effect.Row) is not { } row) || (row.EffectiveDomain is not StateDomain.Ring)) {
            return false;
        }

        if (!TryReadSource(effect, row.Kind, tick, out var raw, out var fault)) {
            if (fault != ExpressionFault.None) {
                RefuseSource(effect, ruleName, tick, preflight, fault);
            }
            return false;
        }

        return Apply(effect: effect, ruleName: ruleName, mutation: new StateMutation.Apply(Transform: new StateTransform.Push(Row: row.Name.Value, Value: raw)), tick: tick, preflight: preflight);
    }

    // One source read per execution pass. Absence/forever skip a direct copy; expression faults remain diagnostic.
    private bool TryReadSource(IValueSourcedEffect source, CellKind kind, ulong tick, out long raw, out ExpressionFault fault) {
        fault = ExpressionFault.None;
        if (source.Expression is { } expression) {
            return TryEvaluateExpression(expression, kind, tick, out raw, out fault);
        }
        if (source.From is { } from) {
            var fact = Read(from, tick);
            raw = 0;
            if (fact.IsAbsent || fact.IsForever) {
                return false;
            }
            raw = fact.ToRaw(kind);
        } else {
            raw = source.RawValue;
        }
        return true;
    }

    private void RefuseSource(EffectFact effect, string ruleName, ulong tick, bool preflight, ExpressionFault fault) {
        if (preflight) {
            m_preflightRejected = true;
        }
        if (fault != ExpressionFault.TableKeyMissing) {
            ReportRefusal(refusal: RuleEffectRefusal.Arithmetic, ruleName: ruleName, effect: effect, tick: tick, detail: DescribeFault(fault));
        }
    }

    // A transaction preflights its branch under one host scope, then commits that scope as one mutation; the steps
    // that submit nothing (a cue, a pose, a body effect) fire afterwards, for real, in order. A refused branch runs
    // onFailure on the same terms.
    private bool FireTransaction(TransactionEffect transaction, string ruleName, ulong tick, ulong stepTicks) {
        if (FireBranch(effect: transaction, effects: transaction.Effects, ruleName: ruleName, tick: tick, stepTicks: stepTicks, applied: out var applied)) {
            return applied;
        }

        var failure = transaction.OnFailure;

        if (failure.Length == 0) {
            return false;
        }

        return FireBranch(effect: transaction, effects: failure, ruleName: ruleName, tick: tick, stepTicks: stepTicks, applied: out applied) && applied;
    }

    private bool FireBranch(EffectFact effect, EffectFact[] effects, string ruleName, ulong tick, ulong stepTicks, out bool applied) {
        applied = false;
        m_preflightRejected = false;
        m_host.BeginPreflight();

        var rejected = false;

        try {
            for (var index = 0; index < effects.Length; index++) {
                _ = Fire(effect: effects[index], ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: true, strict: true);
                if (m_preflightRejected) {
                    rejected = true;
                    break;
                }
            }
        } catch {
            m_host.EndPreflight();
            throw;
        }

        if (rejected) {
            m_host.EndPreflight();
            m_preflightRejected = false;

            return false;
        }

        if (m_host.TryCommitPreflight(tick: tick, reason: out var reason)) {
            applied = true;
        } else if (reason.Length > 0) {
            ReportRefusal(refusal: RuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: effect, tick: tick, detail: reason);

            return false;
        }

        for (var index = 0; index < effects.Length; index++) {
            if (!effects[index].SubmitsMutation) {
                _ = Fire(effect: effects[index], ruleName: ruleName, tick: tick, stepTicks: stepTicks, preflight: false, strict: true);
            }
        }

        return true;
    }

    private bool Apply(EffectFact effect, string ruleName, StateMutation mutation, ulong tick, bool preflight) {
        if (m_host.TryApply(mutation: mutation, tick: tick, preflight: preflight, reason: out var reason)) {
            return true;
        }

        if (preflight) {
            m_preflightRejected = true;
        }
        ReportRefusal(refusal: RuleEffectRefusal.MutationRejected, ruleName: ruleName, effect: effect, tick: tick, detail: reason);

        return false;
    }

    private bool Outcome(EffectOutcome outcome, bool preflight) {
        if (preflight && (outcome == EffectOutcome.Refused)) {
            m_preflightRejected = true;
        }

        return (outcome == EffectOutcome.Applied);
    }

    private static long ScheduleDueTick(ulong tick, long delayTicks, out ExpressionFault fault) {
        var failed = ((delayTicks < 0L) || (tick > ((ulong)(long.MaxValue - Math.Max(val1: 0L, val2: delayTicks)))));

        fault = (failed ? ExpressionFault.Domain : ExpressionFault.None);

        return (failed ? 0L : checked(((long)tick) + delayTicks));
    }
}
