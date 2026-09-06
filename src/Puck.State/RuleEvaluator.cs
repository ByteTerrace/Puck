using System.Runtime.InteropServices;

namespace Puck.State;

/// <summary>Evaluates compiled rules over a host: gates in document order, edge latching per binding, bindings
/// computed before the gate, and every state-neutral effect fired through the host's mutation door. The evaluator
/// is the evaluation in flight — the tick every read answers as of, the bound forEach key and participants, and the
/// rule's bound values — which the host's <see cref="IRuleReader"/> forwards to its operands.</summary>
/// <remarks>Rules in one tick are a sequence, not a simultaneous snapshot: an effect installs immediately, so a
/// later rule's gate reads an earlier rule's same-tick write. Document order is what makes that deterministic.</remarks>
public sealed partial class RuleEvaluator {
    /// <summary>A latch key for a non-integer forEach cell: its position in the row, flagged above any participant
    /// index a host could bind.</summary>
    public const int PositionalLatchBase = 0x4000_0000;

    private readonly IRuleHost m_host;
    private readonly long[] m_bindingValues = new long[RuleCapacity.MaxBindingsPerRule];
    private readonly List<CellName> m_eachKeyScratch = [];

    /// <summary>Initializes a new evaluator over a host.</summary>
    /// <param name="host">The host every read and write goes through.</param>
    public RuleEvaluator(IRuleHost host) {
        ArgumentNullException.ThrowIfNull(argument: host);
        m_host = host;
    }

    /// <summary>Gets or sets whether a rule whose gate closed last time, whose reads carry no host or tick
    /// dependency, and whose every read row's version is unchanged may keep its closed verdict without re-running
    /// its bindings and gate — and whether an unchanged binding may reuse its memoized value. Defaults to
    /// <see langword="true"/>; a law flips it off to prove the skip changes no observable result.</summary>
    public bool SchedulingEnabled { get; set; } = true;

    /// <summary>Gets or sets the simulation tick every read answers as of; each entry point that takes a tick sets it
    /// before reading.</summary>
    public ulong Tick { get; set; }
    /// <summary>Gets or sets the participant index bound to <see cref="BoundKey.Each"/>, or -1.</summary>
    public int BoundEach { get; set; } = -1;
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Each"/>, or <see langword="null"/>.</summary>
    public string? BoundEachKey { get; set; }
    /// <summary>Gets or sets the forEach loop's own 0-based position, or -1 outside a forEach evaluation — what a
    /// host's position-indexed binding resolves against, in the same order <see cref="EachKeys"/> walks.</summary>
    public int BoundEachPosition { get; set; } = -1;
    /// <summary>Gets or sets the participant index bound to <see cref="BoundKey.Left"/>, or -1.</summary>
    public int BoundLeft { get; set; } = -1;
    /// <summary>Gets or sets the participant index bound to <see cref="BoundKey.Right"/>, or -1.</summary>
    public int BoundRight { get; set; } = -1;
    /// <summary>Gets or sets the name of the rule whose evaluation is in flight — set by <see cref="EvaluateOnce"/>,
    /// or by a host evaluating a rule of its own — so a refusal a read draws names its rule.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>Returns the participant index a binding names, or -1.</summary>
    /// <param name="key">The binding.</param>
    public int BoundIndex(BoundKey key) => key switch {
        BoundKey.Each => BoundEach,
        BoundKey.Left => BoundLeft,
        BoundKey.Right => BoundRight,
        _ => -1,
    };
    /// <summary>Returns the value the rule in flight bound at that ordinal.</summary>
    /// <param name="ordinal">The binding's slot.</param>
    public long BindingValue(int ordinal) => m_bindingValues[ordinal];

    /// <summary>Reads one operand as of a tick.</summary>
    /// <param name="operand">The compiled operand.</param>
    /// <param name="tick">The tick.</param>
    public RuleFact Read(OperandFact operand, ulong tick) {
        Tick = tick;

        return operand.Read(reader: m_host);
    }
    /// <summary>Resolves a literal key or a live key indirection as of a tick.</summary>
    /// <param name="key">The literal key.</param>
    /// <param name="keyFrom">The indirection, or <see langword="null"/>.</param>
    /// <param name="tick">The tick.</param>
    public string ResolveKey(string? key, CompiledCellRef? keyFrom, ulong tick) {
        Tick = tick;

        return RuleEvaluation.ResolveKey(reader: m_host, key: key, keyFrom: keyFrom);
    }
    /// <summary>Evaluates a compiled expression as of a tick.</summary>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="value">The raw result.</param>
    /// <returns><see langword="true"/> when the expression evaluated.</returns>
    public bool TryEvaluateExpression(CompiledExpressionToken[] program, CellKind kind, ulong tick, out long value) =>
        TryEvaluateExpression(program: program, kind: kind, tick: tick, value: out value, fault: out _);
    /// <summary>Evaluates a compiled expression as of a tick, naming why it did not evaluate.</summary>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="value">The raw result.</param>
    /// <param name="fault">Why the expression failed, or <see cref="ExpressionFault.None"/>.</param>
    /// <returns><see langword="true"/> when the expression evaluated.</returns>
    public bool TryEvaluateExpression(CompiledExpressionToken[] program, CellKind kind, ulong tick, out long value, out ExpressionFault fault) {
        Tick = tick;

        return RuleEvaluation.TryEvaluateExpression(reader: m_host, program: program, kind: kind, value: out value, fault: out fault);
    }
    /// <summary>Evaluates a compiled gate as of a tick. A table read naming a key its table lacks closes the gate
    /// after reporting itself; a conjunct whose expression faults reads false and reports
    /// <see cref="RuleEffectRefusal.Arithmetic"/> against the rule in flight, so a domain fault is a counted refusal
    /// rather than a gate that silently stopped holding.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="ruleName">The rule the gate belongs to, for the refusal ledger.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    public bool GateOpen(GateToken[] gate, ulong tick, string ruleName, List<string>? trace = null) {
        Tick = tick;
        m_host.TableKeyMissing = false;

        var open = RuleEvaluation.GateHolds(reader: m_host, gate: gate, faulted: out var faulted, trace: trace);

        if (faulted) {
            ReportRefusal(refusal: RuleEffectRefusal.Arithmetic, ruleName: ruleName, effect: "gate", tick: tick, detail: "a gate conjunct's expression overflowed, divided by zero, left a function's domain, or read a fact with no number; the conjunct read false");
        }

        if (m_host.TableKeyMissing) {
            m_host.TableKeyMissing = false;

            return false;
        }

        return open;
    }

    /// <summary>Fills the iteration set of a forEach rule: every cell key of the iterated row in cell order. The
    /// keys are snapshotted before the first evaluation, so an effect minting a cell starts ticking next tick, never
    /// mid-iteration.</summary>
    /// <param name="row">The iterated row.</param>
    /// <param name="into">The caller's scratch list.</param>
    public void EachKeys(string row, List<CellName> into) {
        into.Clear();

        if (m_host.Store.Find(name: row) is { Cells: { } cells }) {
            for (var index = 0; index < cells.Count; index++) {
                into.Add(item: cells[index].Key);
            }
        }
    }

    /// <summary>Evaluates one family of compiled rules in array order, each under its own latch bindings. A rule the
    /// host claims through <see cref="IRuleHost.TryEvaluateOwn"/> is the host's; every other evaluates once, or once
    /// per key of its forEach row.</summary>
    /// <param name="rules">The family, snapshotted by the caller — an effect that recompiles the family does not
    /// change what this tick evaluates.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">How many ticks the step spans.</param>
    /// <returns><see langword="true"/> when any effect installed a mutation.</returns>
    public bool Evaluate(CompiledRule[] rules, RuleLatch latch, ulong tick, ulong stepTicks) {
        var applied = false;

        foreach (var rule in rules) {
            if (m_host.TryEvaluateOwn(rule: rule, latch: latch, tick: tick, stepTicks: stepTicks, applied: out var own)) {
                applied |= own;
                continue;
            }

            var bindings = latch.Bindings(name: rule.Name);

            if (rule.ForEach is { } forEach) {
                // Over "$zones" the keys are the table's own non-empty indices, in index order.
                if (rule.Zones is { } zones && string.Equals(a: forEach, b: RuleFacts.ForEachZones, comparisonType: StringComparison.Ordinal)) {
                    m_eachKeyScratch.Clear();
                    m_eachKeyScratch.AddRange(collection: zones.Indices);
                } else {
                    EachKeys(row: forEach, into: m_eachKeyScratch);
                }
                latch.BeginSweep();

                for (var position = 0; position < m_eachKeyScratch.Count; position++) {
                    var key = m_eachKeyScratch[position];
                    var numeric = StateReader.TryParseCandidateIndex(key: key, index: out var index);
                    BoundEach = (numeric ? index : -1);
                    BoundEachKey = key.Value;
                    BoundEachPosition = position;
                    applied |= EvaluateOnce(
                        rule: rule,
                        latch: latch,
                        bindings: bindings,
                        binding: new LatchKey(Left: (numeric ? index : (PositionalLatchBase | position)), Right: -1),
                        tick: tick,
                        stepTicks: stepTicks
                    );
                }

                BoundEach = -1;
                BoundEachKey = null;
                BoundEachPosition = -1;
                latch.EndSweep(name: rule.Name, bindings: bindings);

                continue;
            }

            applied |= EvaluateOnce(rule: rule, latch: latch, bindings: bindings, binding: LatchKey.None, tick: tick, stepTicks: stepTicks);
        }

        return applied;
    }

    /// <summary>Runs one gate-and-fire under the bindings already in place. Edge fires on the crossing alone and
    /// re-arms only when the gate closes again; Level fires every tick the gate holds. The latch entry is per
    /// binding, never per rule alone; an entry the enclosing sweep does not touch is closed by the sweep.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="bindings">The rule's latch bindings.</param>
    /// <param name="binding">The binding this evaluation runs under.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="stepTicks">How many ticks the step spans.</param>
    /// <returns><see langword="true"/> when any effect installed a mutation.</returns>
    public bool EvaluateOnce(CompiledRule rule, RuleLatch latch, Dictionary<LatchKey, bool> bindings, LatchKey binding, ulong tick, ulong stepTicks) {
        RuleName = rule.Name;

        var trace = BeginTrace(rule: rule, tick: tick);
        var existed = bindings.TryGetValue(key: binding, value: out var wasOpen);
        // Scheduling never runs under a trace: a traced evaluation wants every binding and conjunct computed fresh.
        var schedule = ((SchedulingEnabled && (trace is null)) ? rule.Schedule(reader: m_host) : null);

        if (existed && !wasOpen && (schedule is { Volatile: false } sched) && VersionsMatch(schedule: sched, cached: latch.GateVersions(name: rule.Name, binding: binding))) {
            // Nothing this rule reads has changed since it last closed, and it reads no host or tick fact a version
            // cannot see through — the verdict is still closed, so bindings and gate need not run at all.
            latch.Touch(binding: binding);
            EndTrace(entry: trace);

            return false;
        }

        // Bound values first, in declared order, each visible to the ones after it and to the gate and effects. A
        // binding that cannot evaluate closes the gate for this evaluation and is reported once per category like any
        // effect's arithmetic refusal.
        var bound = (rule.Bindings ?? []);
        var memos = ((schedule is not null) && (bound.Length > 0)) ? latch.BindingMemos(name: rule.Name, binding: binding, count: bound.Length) : null;

        for (var ordinal = 0; ordinal < bound.Length; ordinal++) {
            var declared = bound[ordinal];

            if (memos is { } cached) {
                var bindingSchedule = declared.Schedule(reader: m_host);

                if (!bindingSchedule.Volatile && (cached[ordinal] is { } memo) && VersionsMatch(schedule: bindingSchedule, cached: memo.Versions)) {
                    m_bindingValues[ordinal] = memo.Value;
                    trace?.Bindings.Add(item: $"{declared.Name}={RuleEvaluation.DescribeFact(value: memo.Value, kind: declared.Kind, isForever: false)}");

                    continue;
                }
            }

            if (!TryEvaluateExpression(program: declared.Expression, kind: declared.Kind, tick: tick, value: out var value, fault: out var fault)) {
                trace?.Bindings.Add(item: $"{declared.Name}=refused");
                if (fault != ExpressionFault.TableKeyMissing) {
                    ReportRefusal(refusal: RuleEffectRefusal.Arithmetic, ruleName: rule.Name, effect: $"binding '{declared.Name}'", tick: tick, detail: DescribeFault(fault: fault));
                }
                EndTrace(entry: trace);

                return false;
            }

            m_bindingValues[ordinal] = value;

            if (memos is { } cache) {
                var memo = (cache[ordinal] ??= new RuleLatch.BindingMemo());

                memo.Value = value;
                CaptureVersions(schedule: declared.Schedule(reader: m_host), cache: ref memo.Versions);
            }

            trace?.Bindings.Add(item: $"{declared.Name}={RuleEvaluation.DescribeFact(value: value, kind: declared.Kind, isForever: false)}");
        }

        // Every live zone the rule spells must select an entry, or this evaluation is simply not for these indices —
        // the gate reads closed, nothing is refused, and the trace names what each spelling selected.
        var open = (ZonesSelected(rule: rule, tick: tick, trace: trace?.Zones) && GateOpen(gate: rule.Gate, tick: tick, ruleName: rule.Name, trace: trace?.Conjuncts));
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: bindings, key: binding, exists: out _);

        slot = open;
        latch.Touch(binding: binding);

        if ((schedule is { } closedSchedule) && !open) {
            // The gate closed again: remember what its reads looked like, so an unchanged read next time skips.
            var cache = (latch.GateVersions(name: rule.Name, binding: binding) ?? []);

            CaptureVersions(schedule: closedSchedule, cache: ref cache);
            latch.SetGateVersions(name: rule.Name, binding: binding, versions: cache);
        }

        var fires = (open && ((rule.Mode != ActionTriggerMode.Edge) || !wasOpen));

        if (trace is not null) {
            trace.GateOpen = open;
            trace.EdgeHeld = (open && !fires);
        }

        if (!fires) {
            EndTrace(entry: trace);

            return false;
        }

        var applied = FireEffects(effects: rule.Effects, ruleName: rule.Name, tick: tick, stepTicks: stepTicks);

        EndTrace(entry: trace);

        return applied;
    }

    // Whether every row a schedule reads reports the same version it did when the cache was captured — a mismatch,
    // a schedule the host has flagged volatile, or a host that cannot answer a row's version at all all count as
    // "unproven", which is the safe default: evaluate in full rather than trust a stale or unanswerable cache.
    private bool VersionsMatch(RuleSchedule schedule, ulong[]? cached) {
        if (schedule.Volatile || (cached is null) || (cached.Length != schedule.Rows.Length)) {
            return false;
        }

        for (var index = 0; index < schedule.Rows.Length; index++) {
            if (!m_host.TryRowVersion(row: schedule.Rows[index], version: out var version) || (version != cached[index])) {
                return false;
            }
        }

        return true;
    }
    // Overwrites a cached version array in place, reusing it when its length already matches the schedule's row
    // count; a row the host cannot answer for is recorded as zero, which VersionsMatch never trusts on its own,
    // since it re-queries TryRowVersion rather than comparing against a cached placeholder.
    private void CaptureVersions(RuleSchedule schedule, ref ulong[] cache) {
        if (cache.Length != schedule.Rows.Length) {
            cache = new ulong[schedule.Rows.Length];
        }

        for (var index = 0; index < schedule.Rows.Length; index++) {
            cache[index] = (m_host.TryRowVersion(row: schedule.Rows[index], version: out var version) ? version : 0UL);
        }
    }

    private bool ZonesSelected(CompiledRule rule, ulong tick, List<string>? trace) {
        if (rule.Zones is not { References.Count: > 0 } zones) {
            return true;
        }
        Tick = tick;
        var selected = true;
        foreach (var reference in zones.References) {
            var name = reference.ResolveName(reader: m_host);
            var found = !ReferenceEquals(objA: name, objB: reference.Spelling);
            selected &= found;
            trace?.Add(item: $"{reference.Spelling} -> {(found ? name : "none")}");
            if (!found && (trace is null)) {
                return false;
            }
        }
        return selected;
    }

    private static string DescribeFault(ExpressionFault fault) => fault switch {
        ExpressionFault.Forever => "the expression read a fact with no number (a forever fact)",
        ExpressionFault.Absent => "the expression read through a dynamic key that named no cell (an empty zone's endpoint)",
        _ => "the expression overflowed, divided by zero, left a function's domain, or produced an invalid stack result",
    };
}
