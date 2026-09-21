using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.State.Rules;

/// <summary>What one evaluation of one rule did.</summary>
public enum RuleOutcome : byte {
    /// <summary>The gate closed, the edge was already held, or the firing moved nothing.</summary>
    Idle,

    /// <summary>The firing committed and moved the arena.</summary>
    Fired,

    /// <summary>An effect refused, so the firing's scope rewound and one refusal was counted.</summary>
    Refused,
}
/// <summary>What a host implements to claim a rule kind only it can evaluate — a compiled record that widened
/// <see cref="CompiledRule"/> with a host concept. The host runs each evaluation back through
/// <see cref="RuleEvaluator.EvaluateOnce"/> under the latch bindings it chooses, so the edge latch, the trace and
/// the refusal ledger stay one mechanism.</summary>
public interface IRuleOwner {
    /// <summary>Evaluates a rule kind only the host understands.</summary>
    /// <param name="evaluator">The evaluator driving the family.</param>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether any effect moved the arena.</param>
    /// <returns><see langword="true"/> when the host evaluated the rule; <see langword="false"/> hands it to the
    /// library's own iterate-or-once evaluation.</returns>
    bool TryEvaluateOwn(RuleEvaluator evaluator, CompiledRule rule, RuleLatch latch, ulong stepTicks, out bool applied);
}
/// <summary>Evaluates compiled rules over a host: gates in document order, edge latching per binding, bindings
/// computed before the gate, and one arena journal scope per firing. The evaluator is the evaluation in flight —
/// the bound iteration key and the rule's bound values — which it writes onto the host every operand reads
/// through.</summary>
/// <remarks>
/// <para>Rules in one tick are a sequence, not a simultaneous snapshot: a committed firing is visible to the next
/// rule's gate. Document order is what makes that deterministic.</para>
/// <para>The tick pair every read answers as of is the host's, never the evaluator's: a caller advances its host to
/// the tick it wants and then evaluates.</para>
/// </remarks>
public sealed partial class RuleEvaluator {
    private readonly IEffectHost m_host;

    private readonly List<CellKey> m_eachKeyScratch = [];
    private readonly List<StateInstanceHandle>?[] m_poolHandleScratch = new List<StateInstanceHandle>[StateCapacity.MaxInstanceBindings];
    private readonly List<CellAccess> m_readScratch = [];
    private readonly ConditionalWeakTable<object, RuleSchedule> m_schedules = [];

    /// <summary>Initializes a new evaluator over a host.</summary>
    /// <param name="host">The host every read and write goes through.</param>
    public RuleEvaluator(IEffectHost host) {
        ArgumentNullException.ThrowIfNull(argument: host);

        m_host = host;
    }

    /// <summary>Gets the cell key bound to <see cref="BoundKey.Each"/> for the evaluation in flight.</summary>
    public CellKey BoundEachKey => m_host.BoundEachKey;
    /// <summary>Gets the host every read and write goes through.</summary>
    public IEffectHost Host => m_host;

    /// <summary>Sets one public lexical instance register for a host-owned evaluation such as an interaction. The
    /// caller keeps it live through <see cref="EvaluateOnce"/> and clears it in a <see langword="finally"/> block.</summary>
    public bool TrySetInstanceBinding(int register, StateInstanceHandle handle) {
        var bindings = m_host.InstanceBindings;

        if (((uint)register) >= ((uint)bindings.Length)) {
            return false;
        }
        bindings[register] = handle;
        return true;
    }
    /// <summary>Clears one lexical instance register after its owner finishes evaluating.</summary>
    public void ClearInstanceBinding(int register) {
        var bindings = m_host.InstanceBindings;

        if (((uint)register) < ((uint)bindings.Length)) {
            bindings[register] = default;
        }
    }

    /// <summary>Gets or sets whether a rule whose gate closed last time, whose reads carry no host or tick
    /// dependency, and whose every read row's version is unchanged may keep its closed verdict without re-running
    /// its bindings and gate — and whether an unchanged binding may reuse its memoized value. Defaults to
    /// <see langword="true"/>; a law flips it off to prove the skip changes no observable result.</summary>
    public bool SchedulingEnabled { get; set; } = true;

    // The schedule is a function of the compiled rule and the layout, both of which outlive one tick, so it is
    // derived once per compiled instance and held weakly beside it.
    private RuleSchedule Schedule(CompiledRule rule) {
        if (m_schedules.TryGetValue(
            key: rule,
            value: out var cached
        )) {
            return cached;
        }

        m_readScratch.Clear();
        rule.CollectReads(into: m_readScratch);

        var schedule = RuleSchedule.Build(
            needs: rule.Needs,
            reader: m_host,
            reads: m_readScratch
        );

        m_schedules.Add(
            key: rule,
            value: schedule
        );

        return schedule;
    }
    private RuleSchedule Schedule(CompiledRuleLocal binding) {
        if (m_schedules.TryGetValue(
            key: binding,
            value: out var cached
        )) {
            return cached;
        }

        m_readScratch.Clear();
        RuleDataflow.CollectExpression(
            into: m_readScratch,
            tokens: binding.Expression
        );

        var needs = new RuleNeedsBuilder();

        RuleDataflow.CollectExpressionSchedulingFacts(
            into: needs,
            tokens: binding.Expression
        );

        var schedule = RuleSchedule.Build(
            needs: needs.Build(),
            reader: m_host,
            reads: m_readScratch
        );

        m_schedules.Add(
            key: binding,
            value: schedule
        );

        return schedule;
    }
    // Whether every row a schedule reads reports the same version it did when the cache was captured. A mismatch, a
    // volatile schedule, or a row the host cannot answer for all count as unproven, which is the safe default.
    private bool VersionsMatch(RuleSchedule schedule, ulong[]? cached) {
        if (
            schedule.Volatile ||
            (cached is null) ||
            (cached.Length != schedule.Rows.Length)
        ) {
            return false;
        }

        for (var index = 0; (index < schedule.Rows.Length); index++) {
            if (
                !m_host.TryRowVersion(
                rowOrdinal: schedule.Rows[index],
                version: out var version
            ) ||
                (version != cached[index])
            ) {
                return false;
            }
        }

        return true;
    }
    // Overwrites a cached version array in place, reusing it when its length already matches the schedule's row
    // count; a row the host cannot answer for is recorded as zero, which VersionsMatch never trusts on its own.
    private void CaptureVersions(RuleSchedule schedule, ref ulong[] cache) {
        if (cache.Length != schedule.Rows.Length) {
            cache = new ulong[schedule.Rows.Length];
        }

        for (var index = 0; (index < schedule.Rows.Length); index++) {
            cache[index] = (m_host.TryRowVersion(
                rowOrdinal: schedule.Rows[index],
                version: out var version
            )
                ? version
                : 0UL
            );
        }
    }
    private bool RowsSelected(CompiledRule rule, List<string>? trace) {
        if (rule.Zones is not { References.Count: > 0 } zones) {
            return true;
        }

        var selected = true;

        foreach (var reference in zones.References) {
            var found = reference.TryResolve(
                reader: m_host,
                rowOrdinal: out var ordinal
            );

            selected &= found;
            trace?.Add(item: $"{reference.Spelling} -> {(found
                ? m_host.Catalog.Descriptors[ordinal].Name
                : "none")}");
            if (
                !found &&
                (trace is null)
            ) {
                return false;
            }
        }

        return selected;
    }
    // Snapshots the iterated row's cell keys in cell order before a sweep. Cells minted during the sweep join the
    // next sweep; removals and reordering do not change what this sweep binds.
    private void EachKeys(CompiledRule rule) {
        m_eachKeyScratch.Clear();

        if (rule.ForEachZones) {
            if (rule.Zones is { } zones) {
                m_eachKeyScratch.AddRange(collection: zones.Indices);
            }

            return;
        }

        var arena = m_host.Arena;
        var cursor = 0;

        while (arena.TryNextCell(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: rule.ForEachOrdinal
        )) {
            m_eachKeyScratch.Add(item: key);
        }
    }
    private List<StateInstanceHandle> PoolHandles(StatePoolDescriptor pool, int bindingSlot) {
        var scratch = (m_poolHandleScratch[bindingSlot] ??= []);
        var arena = m_host.Arena;

        CollectionsMarshal.SetCount(list: scratch, count: arena.CellCount(rowOrdinal: pool.DomainRowOrdinal));
        _ = arena.CopyPoolSnapshot(poolOrdinal: pool.Ordinal, destination: CollectionsMarshal.AsSpan(list: scratch));
        return scratch;
    }

    /// <summary>Evaluates one family of compiled rules in array order, each under its own latch bindings. A rule the
    /// host claims through <see cref="IRuleOwner.TryEvaluateOwn"/> is the host's; every other evaluates once, or
    /// once per key of the row it iterates.</summary>
    /// <param name="rules">The family, snapshotted by the caller — an effect that recompiles the family does not
    /// change what this tick evaluates.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any firing moved the arena.</returns>
    public bool Evaluate(CompiledRule[] rules, RuleLatch latch, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var applied = false;

        foreach (var rule in rules) {
            applied |= EvaluateRule(
                latch: latch,
                rule: rule,
                stepTicks: stepTicks
            );
        }

        return applied;
    }
    /// <summary>Evaluates one compiled rule: once, once per key of the row it iterates, or through the host when the
    /// host claims the rule kind.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any firing moved the arena.</returns>
    public bool EvaluateRule(CompiledRule rule, RuleLatch latch, ulong stepTicks) {
        _ = EvaluateRule(
            applied: out var applied,
            latch: latch,
            rule: rule,
            stepTicks: stepTicks
        );

        return applied;
    }
    /// <summary>Evaluates one compiled rule and reports what its evaluations did as a whole: refused when any
    /// evaluation rewound, fired when any committed, and idle otherwise.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether any firing moved the arena.</param>
    /// <returns>The aggregate outcome.</returns>
    public RuleOutcome EvaluateRule(CompiledRule rule, RuleLatch latch, ulong stepTicks, out bool applied) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rule);

        applied = false;

        if (
            (m_host is IRuleOwner owner) &&
            owner.TryEvaluateOwn(
            applied: out var own,
            evaluator: this,
            latch: latch,
            rule: rule,
            stepTicks: stepTicks
        )
        ) {
            applied = own;

            return (own
                ? RuleOutcome.Fired
                : RuleOutcome.Idle
            );
        }

        var bindings = latch.Bindings(name: rule.Name);

        if ((rule.ForEachOrdinal < 0) && !rule.ForEachZones && (rule.PoolForEach is null)) {
            return EvaluateOnce(
                applied: out applied,
                binding: LatchKey.None,
                bindings: bindings,
                latch: latch,
                rule: rule,
                stepTicks: stepTicks
            );
        }

        if (rule.PoolForEach is { } pool) {
            var snapshot = PoolHandles(pool: pool, bindingSlot: rule.PoolBindingSlot);

            latch.BeginSweep();
            var poolAggregate = RuleOutcome.Idle;
            var bindingsSpan = m_host.InstanceBindings;

            foreach (var handle in snapshot) {
                if (
                    (((uint)rule.PoolBindingSlot) >= ((uint)bindingsSpan.Length)) ||
                    !m_host.Arena.TryResolve(handle: handle, position: out _) ||
                    !m_host.Arena.TryPoolKey(slot: handle.Slot, key: out var slotKey)
                ) {
                    continue;
                }
                bindingsSpan[rule.PoolBindingSlot] = handle;
                var outcome = EvaluateOnce(applied: out var moved, binding: new LatchKey(Left: slotKey.Ordinal, Right: -1, LeftGeneration: handle.Generation), bindings: bindings, latch: latch, rule: rule, stepTicks: stepTicks);

                applied |= moved;
                if ((outcome == RuleOutcome.Refused) || ((outcome == RuleOutcome.Fired) && (poolAggregate == RuleOutcome.Idle))) {
                    poolAggregate = outcome;
                }
            }
            if (((uint)rule.PoolBindingSlot) < ((uint)bindingsSpan.Length)) {
                bindingsSpan[rule.PoolBindingSlot] = default;
            }
            latch.EndSweep(bindings: bindings, name: rule.Name);
            return poolAggregate;
        }

        EachKeys(rule: rule);
        latch.BeginSweep();

        var aggregate = RuleOutcome.Idle;

        foreach (var key in m_eachKeyScratch) {
            m_host.BoundEachKey = key;

            var outcome = EvaluateOnce(
                applied: out var moved,
                binding: new LatchKey(
                    Left: key.Ordinal,
                    Right: -1
                ),
                bindings: bindings,
                latch: latch,
                rule: rule,
                stepTicks: stepTicks
            );

            applied |= moved;
            if (
                (outcome == RuleOutcome.Refused) ||
                ((outcome == RuleOutcome.Fired) && (aggregate == RuleOutcome.Idle))
            ) {
                aggregate = outcome;
            }
        }

        m_host.BoundEachKey = default;
        latch.EndSweep(
            bindings: bindings,
            name: rule.Name
        );

        return aggregate;
    }
    /// <summary>Runs one gate-and-fire under the bindings already in place. Edge fires on the crossing alone and
    /// re-arms only when the gate closes again; Level fires every tick the gate holds. The latch entry is per
    /// binding, never per rule alone; an entry the enclosing sweep does not touch is closed by the sweep.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="bindings">The rule's latch bindings.</param>
    /// <param name="binding">The binding this evaluation runs under.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether the firing moved the arena or emitted through the host.</param>
    /// <returns>What the evaluation did.</returns>
    public RuleOutcome EvaluateOnce(CompiledRule rule, RuleLatch latch, Dictionary<LatchKey, bool> bindings, LatchKey binding, ulong stepTicks, out bool applied) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rule);

        applied = false;

        var tick = m_host.Tick;
        var trace = BeginTrace(
            rule: rule,
            tick: tick
        );
        var existed = bindings.TryGetValue(
            key: binding,
            value: out var wasOpen
        );
        // Scheduling never runs under a trace: a traced evaluation wants every binding and conjunct computed fresh.
        var schedule = ((SchedulingEnabled && (trace is null))
            ? Schedule(rule: rule)
            : null
        );

        if (
            existed &&
            !wasOpen &&
            (schedule is { Volatile: false } closed) &&
            VersionsMatch(
            cached: latch.GateVersions(
                binding: binding,
                name: rule.Name,
                owner: closed
            ),
            schedule: closed
        )
        ) {
            // Nothing this rule reads has changed since it last closed, and it reads no host or tick fact a version
            // cannot see through — the verdict is still closed, so bindings and gate need not run at all.
            latch.Touch(binding: binding);
            EndTrace(entry: trace);

            return RuleOutcome.Idle;
        }

        if (!EvaluateBindings(
            binding: binding,
            latch: latch,
            rule: rule,
            schedule: schedule,
            tick: tick,
            trace: trace
        )) {
            EndTrace(entry: trace);

            return RuleOutcome.Idle;
        }

        // Every live row the rule spells must select a row, or this evaluation is simply not for these indices: the
        // gate reads closed, nothing is refused, and the trace names what each spelling selected.
        var open = (RowsSelected(
            rule: rule,
            trace: trace?.Zones
        ) && GateOpen(
            faulted: out _,
            gate: rule.Gate,
            ruleName: rule.Name,
            trace: trace?.Conjuncts
        ));

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: bindings,
            exists: out _,
            key: binding
        );

        slot = open;
        latch.Touch(binding: binding);

        if (
            (schedule is { } reads) &&
            !open
        ) {
            // The gate closed again: remember what its reads looked like, so an unchanged read next time skips.
            var cache = (latch.GateVersions(
                binding: binding,
                name: rule.Name,
                owner: reads
            ) ?? []);

            CaptureVersions(
                cache: ref cache,
                schedule: reads
            );
            latch.SetGateVersions(
                binding: binding,
                name: rule.Name,
                owner: reads,
                versions: cache
            );
        }

        var fires = (open && ((rule.Mode != ActionTriggerMode.Edge) || !wasOpen));

        if (trace is not null) {
            trace.EdgeHeld = (open && !fires);
            trace.GateOpen = open;
        }

        if (!fires) {
            EndTrace(entry: trace);

            return RuleOutcome.Idle;
        }

        var outcome = FireRule(
            applied: out applied,
            rule: rule,
            stepTicks: stepTicks,
            tick: tick
        );

        if ((outcome == RuleOutcome.Fired) && (rule.Effects is [RewindTurnEffect])) {
            latch.InvalidateScheduler();
        }

        EndTrace(entry: trace);

        return outcome;
    }
    /// <summary>Evaluates a compiled gate for the evaluation in flight, reporting whether it faulted — a conjunct's
    /// expression overflowed, left a function's domain, or read a fact with no number or no cell. An <c>if</c>
    /// effect's condition uses this to distinguish a genuinely false condition from one that could not evaluate,
    /// which runs neither branch.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="ruleName">The rule the gate belongs to, for the refusal ledger.</param>
    /// <param name="faulted">Whether some conjunct could not evaluate; already reported against the rule in
    /// flight.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    /// <returns><see langword="true"/> when the gate holds.</returns>
    public bool GateOpen(GateToken[] gate, string ruleName, out bool faulted, List<string>? trace = null) {
        var open = RuleEvaluation.GateHolds(
            fault: out var fault,
            gate: gate,
            reader: m_host,
            trace: trace
        );

        faulted = (fault != ExpressionFault.None);
        if (faulted) {
            ReportRefusal(
                detail: $"{RuleEvaluation.DescribeFault(fault: fault)}; the conjunct read false",
                effect: "gate",
                refusal: RuleEffectRefusal.Arithmetic,
                ruleName: ruleName,
                tick: m_host.Tick
            );
        }

        return open;
    }

    // Bound values first, in declared order, each visible to the ones after it and to the gate and effects. A
    // binding that cannot evaluate closes the gate for this evaluation and is reported once per category like any
    // effect's arithmetic refusal.
    private bool EvaluateBindings(CompiledRule rule, RuleLatch latch, LatchKey binding, RuleSchedule? schedule, ulong tick, RuleTraceEvaluation? trace) {
        var declared = (rule.Locals ?? []);

        if (declared.Length == 0) {
            return true;
        }

        var memos = ((schedule is not null)
            ? latch.BindingMemos(
                binding: binding,
                count: declared.Length,
                name: rule.Name
            )
            : null
        );

        for (var ordinal = 0; (ordinal < declared.Length); ordinal++) {
            var bound = declared[ordinal];

            if (memos is { } cached) {
                var bindingSchedule = Schedule(binding: bound);

                if (
                    !bindingSchedule.Volatile &&
                    (cached[ordinal] is { } memo) &&
                    ReferenceEquals(
                    objA: memo.Owner,
                    objB: bindingSchedule
                ) &&
                    VersionsMatch(
                    cached: memo.Versions,
                    schedule: bindingSchedule
                )
                ) {
                    m_host.Locals[ordinal] = memo.Value;
                    trace?.Locals.Add(item: $"{bound.Name}={RuleEvaluation.DescribeFact(
                        isForever: false,
                        kind: bound.Kind,
                        value: memo.Value
                    )}");

                    continue;
                }
            }

            if (!RuleExpressions.TryEvaluate(
                fault: out var fault,
                kind: bound.CarrierKind,
                program: bound.Expression,
                reader: m_host,
                value: out var value
            )) {
                trace?.Locals.Add(item: $"{bound.Name}=refused");
                ReportRefusal(
                    detail: RuleEvaluation.DescribeFault(fault: fault),
                    effect: $"binding '{bound.Name}'",
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: rule.Name,
                    tick: tick
                );

                return false;
            }

            m_host.Locals[ordinal] = value;

            if (memos is { } cache) {
                var memo = (cache[ordinal] ??= new RuleLatch.BindingMemo());
                var bindingSchedule = Schedule(binding: bound);
                var versions = memo.Versions;

                CaptureVersions(
                    cache: ref versions,
                    schedule: bindingSchedule
                );
                memo.Owner = bindingSchedule;
                memo.Value = value;
                memo.Versions = versions;
            }

            trace?.Locals.Add(item: $"{bound.Name}={RuleEvaluation.DescribeFact(
                isForever: false,
                kind: bound.Kind,
                value: value
            )}");
        }

        return true;
    }
}
