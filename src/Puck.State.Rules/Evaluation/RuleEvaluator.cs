using System.Runtime.InteropServices;

namespace Puck.State.Rules;

/// <summary>What one evaluation of one rule did.</summary>
public enum RuleOutcome : byte {
    /// <summary>The gate closed, the edge was already held, or the firing moved nothing.</summary>
    Idle,

    /// <summary>The firing committed, or a Level firing proven to change nothing was not run again.</summary>
    Fired,

    /// <summary>An effect refused, so the firing's scope rewound and one refusal was counted.</summary>
    Refused,

    /// <summary>A step of the evaluation faulted, so the firing's scope rewound and its latch held. Distinct from
    /// <see cref="Refused"/>: a group's Skip policy skips a refusal and holds on a fault.</summary>
    Faulted,
}
/// <summary>What evaluating a rule's bound values answered. The zero value is <see cref="Faulted"/>, so a result nothing
/// set refuses.</summary>
public enum BindingsResult : byte {
    /// <summary>A binding overflowed, divided by zero or read a fact with no number: the evaluation decided nothing.</summary>
    Faulted = 0,

    /// <summary>A binding read an absence: this evaluation is closed, and nothing was decided.</summary>
    Absent,

    /// <summary>Every binding evaluated.</summary>
    Bound,
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
    /// <param name="bindings">The already resolved state of this rule.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether any effect moved the arena.</param>
    /// <param name="outcome">What the host's evaluations did as a whole, by the precedence
    /// <see cref="RuleEvaluator.Evaluate(CompiledRule[], RuleLatch, ulong, out bool)"/> aggregates by.</param>
    /// <returns><see langword="true"/> when the host evaluated the rule; <see langword="false"/> hands it to the
    /// library's own iterate-or-once evaluation.</returns>
    [Replaces(root: typeof(StateArena))]
    bool TryEvaluateOwn(RuleEvaluator evaluator, CompiledRule rule, RuleLatch latch, RuleLatch.RuleState bindings, ulong stepTicks, out bool applied, out RuleOutcome outcome);
    /// <summary>Returns whether <see cref="TryEvaluateOwn"/> claims a rule: the same answer it gives for that rule on
    /// every tick. The owner can name its binding dependencies through <see cref="TryScheduleIteration"/>.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns><see langword="true"/> when the host evaluates the rule itself.</returns>
    bool Owns(CompiledRule rule);
    /// <summary>Names the rows and host facts that determine this owned rule's binding set.</summary>
    bool TryScheduleIteration(CompiledRule rule, List<int> rows, List<FactRead> facts) => false;
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
public sealed partial class RuleEvaluator : Puck.Abstractions.Counting.IWorkCounterSource {
    private readonly IEffectHost m_host;
    private readonly IRuleOwner? m_owner;

    private Puck.Abstractions.Counting.WorkCount m_evaluations;
    private Puck.Abstractions.Counting.WorkCount m_firings;
    private Puck.Abstractions.Counting.WorkCount m_skips;
    // Whether the evaluation EvaluateOnceCore last ran must be judged again next tick whatever moves: a volatile
    // schedule, a Level gate that held, a refusal, or a trace. A due list keeps such a binding due.
    private bool m_rerun;
    // Whether any evaluation since a due list's visit began must be judged again next tick.
    private bool m_anyRerun;

    // The earliest deadline any verdict a due list's visit kept or captured must be judged again at.
    private ulong m_minWake = ulong.MaxValue;
    private readonly List<int> m_bodyIndexScratch = [];
    private readonly List<CellKey> m_eachKeyScratch = [];
    private readonly List<StateInstanceHandle>?[] m_poolHandleScratch = new List<StateInstanceHandle>[StateCapacity.MaxInstanceBindings];
    private readonly List<CellAccess> m_readScratch = [];
    private readonly List<int> m_writtenRows = [];

    private Puck.Abstractions.Counting.WorkCount m_settled;
    // How many firings committed through this evaluator, by whichever door: what tells a firing that ran from one a
    // schedule answered for.
    private long m_firingsRun;
    // Whether the commit Commit last made did nothing on the host but close the arena's scope.
    private bool m_commitArenaOnly;
    private bool[] m_writtenFlags = [];

    /// <summary>Initializes a new evaluator over a host.</summary>
    /// <param name="host">The host every read and write goes through.</param>
    /// <param name="additionalFamilies">Latches for additional due-list passes sharing this host's moved-fact feed.</param>
    public RuleEvaluator(IEffectHost host, params RuleLatch[] additionalFamilies) {
        ArgumentNullException.ThrowIfNull(argument: host);

        m_host = host;
        m_owner = (host as IRuleOwner);
        m_additionalFamilies = additionalFamilies;
        m_dueLists = new RuleDueList?[additionalFamilies.Length + 1];
    }

    /// <inheritdoc/>
    string Puck.Abstractions.Counting.IWorkCounterSource.Name =>
        RuleWorkKinds.SourceName;
    /// <inheritdoc/>
    ReadOnlySpan<Puck.Abstractions.Counting.WorkKind> Puck.Abstractions.Counting.IWorkCounterSource.WorkKinds =>
        RuleWorkKinds.Kinds;

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

    /// <inheritdoc/>
    bool Puck.Abstractions.Counting.IWorkCounterSource.TryRead(Puck.Abstractions.Counting.WorkKind kind, out long value) {
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Evaluations)) {
            value = m_evaluations.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Skips)) {
            value = m_skips.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Settled)) {
            value = m_settled.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Firings)) {
            value = m_firings.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Due)) {
            value = m_due.Value;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: RuleWorkKinds.Elided)) {
            value = m_elided.Value;

            return true;
        }

        value = 0L;

        return false;
    }

    /// <summary>Clears one lexical instance register after its owner finishes evaluating.</summary>
    public void ClearInstanceBinding(int register) {
        var bindings = m_host.InstanceBindings;

        if (((uint)register) < ((uint)bindings.Length)) {
            bindings[register] = default;
        }
    }

    /// <summary>Gets or sets whether a verdict whose schedule names every dependency, each at the version it was
    /// captured at, may stand without running again: a closed gate or a held Edge keeps its verdict without its
    /// bindings and gate, a Level firing that changed nothing is not fired again, and an unchanged binding reuses its
    /// memoized value. Defaults to <see langword="true"/>; a law flips it off to prove the skip changes no observable
    /// result.</summary>
    public bool SchedulingEnabled { get; set; } = true;
    /// <summary>Gets or sets whether a Level firing its schedule would answer for is fired anyway and held to
    /// changing nothing again: the check that the skip is invisible. A firing that then moves a row, queues an arm,
    /// refuses or closes is recorded in <see cref="StaleQuiescence"/>. Defaults to <see langword="false"/>.</summary>
    public bool QuiescenceChecked { get; set; }
    /// <summary>Gets the first Level firing <see cref="QuiescenceChecked"/> found that its schedule answered for and
    /// that did not settle again, naming the rule, the tick and the schedule; <see langword="null"/> when every one
    /// did.</summary>
    public string? StaleQuiescence { get; private set; }

    /// <summary>Returns the read schedule this evaluator holds for a rule, deriving it when the rule has not been
    /// scheduled yet: what the measured sheet and <c>--why</c> print beside a rule's counts.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch, which holds the rule's state.</param>
    /// <returns>The schedule.</returns>
    public RuleSchedule ScheduleOf(CompiledRule rule, RuleLatch latch) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: latch);

        return Schedule(rule: rule, state: latch.Rule(name: rule.Name));
    }

    /// <summary>Returns the schedule a Level firing of a rule stands on once it has changed nothing: every row the
    /// firing reads or writes and every host fact it observes.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch, which holds the rule's state.</param>
    /// <returns>The schedule, or <see langword="null"/> for a rule whose firing is never taken as settled: an Edge,
    /// or a group rewind.</returns>
    public RuleSchedule? FiringScheduleOf(CompiledRule rule, RuleLatch latch) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: latch);

        var state = latch.Rule(name: rule.Name);

        _ = Schedule(rule: rule, state: state);

        return state.FiringSchedule;
    }

    // A rule's and its locals' schedules are a function of the compiled rule, the layout and what the host answers
    // for its facts, all of which outlive one tick, so they are derived once per compiled instance and host fact
    // epoch and held in the rule's latch state beside it.
    private RuleSchedule Schedule(CompiledRule rule, RuleLatch.RuleState state) {
        if (
            ReferenceEquals(objA: state.ScheduleRule, objB: rule) &&
            ReferenceEquals(objA: state.ScheduleEvaluator, objB: this) &&
            (state.ScheduleEpoch == m_host.FactScheduleEpoch)
        ) {
            return state.Schedule!;
        }

        m_readScratch.Clear();

        var needs = new RuleNeedsBuilder();
        var deadline = RuleDeadline.Find(gate: rule.Gate);

        RuleDataflow.CollectJudgement(
            deadline: deadline,
            facts: needs,
            reads: m_readScratch,
            rule: rule
        );

        var schedule = RuleSchedule.Build(
            deadline: deadline,
            facts: needs.FacetFacts,
            iteratesBodies: rule.ForEachBodies,
            iterationRow: rule.ForEachOrdinal,
            iterationVersioned: ((rule.PoolForEach is null) && !rule.ForEachZones && (rule.Iteration is null)),
            needs: needs.Build(),
            reader: m_host,
            reads: m_readScratch
        );
        if ((m_owner is { } owner) && owner.Owns(rule: rule)) {
            var rows = new List<int>();
            var facts = new List<FactRead>();
            var tracked = owner.TryScheduleIteration(rule: rule, rows: rows, facts: facts);
            schedule = schedule with { IterationRows = [.. rows], IterationFacts = [.. facts], IterationVersioned = tracked };
        }
        var locals = (rule.Locals ?? []);
        var localSchedules = new RuleSchedule[locals.Length];

        for (var ordinal = 0; (ordinal < locals.Length); ordinal++) {
            localSchedules[ordinal] = Schedule(binding: locals[ordinal]);
        }

        state.FiringSchedule = FiringSchedule(deadline: deadline, rule: rule);
        state.Schedule = schedule;
        state.LocalSchedules = localSchedules;
        state.ScheduleRule = rule;
        state.ScheduleEvaluator = this;
        state.ScheduleEpoch = m_host.FactScheduleEpoch;

        return schedule;
    }
    // Everything one firing of a rule observes or writes (RuleDataflow.CollectFiring), for the rules whose firing can
    // be taken as settled: a Level rule fires every tick its gate holds, so a firing that changed nothing is the one
    // worth not repeating. An Edge fires once per crossing, and a group rewind restores state no scope encloses.
    private RuleSchedule? FiringSchedule(CompiledRule rule, RuleDeadline? deadline) {
        if ((rule.Mode == ActionTriggerMode.Edge) || (rule.Effects is [RewindGroupEffect])) {
            return null;
        }

        m_readScratch.Clear();

        var needs = new RuleNeedsBuilder();

        RuleDataflow.CollectFiring(
            deadline: deadline,
            facts: needs,
            reads: m_readScratch,
            rule: rule
        );

        return RuleSchedule.Build(
            deadline: deadline,
            facts: needs.FacetFacts,
            needs: needs.Build(),
            reader: m_host,
            reads: m_readScratch
        );
    }
    private RuleSchedule Schedule(CompiledRuleLocal binding) {
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

        return RuleSchedule.Build(
            facts: needs.FacetFacts,
            needs: needs.Build(),
            reader: m_host,
            reads: m_readScratch
        );
    }
    // The one door every elision and reuse passes: whether every dependency a schedule names — each row, then each
    // host fact, read for the body bound now where the fact is read for $each — reports the version it did when the
    // cache was captured. A volatile schedule, a mismatch, or a dependency the host cannot answer for all count as
    // unproven, which is the safe default.
    private bool VersionsMatch(RuleSchedule schedule, ulong[]? cached) {
        if (
            schedule.Volatile ||
            (cached is null) ||
            (cached.Length != schedule.Dependencies)
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
        for (var index = 0; (index < schedule.Facts.Length); index++) {
            if (!TryFactVersion(fact: schedule.Facts[index], version: out var version) || (version != cached[(schedule.Rows.Length + index)])) {
                return false;
            }
        }

        return true;
    }
    // Overwrites a cached version array in place, reusing it when its length already matches the schedule's
    // dependency count; a dependency the host cannot answer for is recorded as zero, which VersionsMatch never trusts on
    // its own.
    private void CaptureVersions(RuleSchedule schedule, ref ulong[] cache) {
        if (cache.Length != schedule.Dependencies) {
            cache = new ulong[schedule.Dependencies];
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
        for (var index = 0; (index < schedule.Facts.Length); index++) {
            cache[(schedule.Rows.Length + index)] = (TryFactVersion(fact: schedule.Facts[index], version: out var version) ? version : 0UL);
        }
    }
    // One fact's version for the evaluation in flight; the host resolves a $each read against the binding in flight,
    // since only its source knows what $each names (a body index, a placement id).
    private bool TryFactVersion(FactRead fact, out ulong version) => m_host.TryFactVersion(
        body: fact.Body,
        source: fact.Source,
        version: out version
    );
    // Open when every live row the rule spells selects a row, Closed when one selects none, Faulted when an index
    // faulted.
    private Verdict RowsSelected(CompiledRule rule, List<string>? trace) {
        if (rule.Zones is not { References.Count: > 0 } zones) {
            return Verdict.Open;
        }

        var selected = true;
        var faulted = false;

        foreach (var reference in zones.References) {
            // A faulted index selects no row, so the rule is not for this evaluation either way; the trace names
            // the fault apart from an index that selects none.
            var resolved = reference.Resolve(reader: m_host);
            var found = resolved.TryNamed(value: out var ordinal);

            if (resolved.Case == FaultCase.Faulted) {
                faulted = true;
                ReportRefusal(
                    detail: $"{RuleReads.KeyFaultReason}; '{reference.Spelling}' selects no row, and the gate decided nothing",
                    effect: "gate",
                    refusal: RuleEffectRefusal.Arithmetic,
                    ruleName: rule.Name,
                    tick: m_host.Tick
                );
            }
            selected &= found;
            trace?.Add(item: $"{reference.Spelling} -> {(found
                ? m_host.Catalog.Descriptors[ordinal].Name
                : ((resolved.Case == FaultCase.Faulted) ? "fault" : "none"))}");
            if (
                !found &&
                (trace is null)
            ) {
                return (faulted
                    ? Verdict.Faulted
                    : Verdict.Closed);
            }
        }

        return (faulted
            ? Verdict.Faulted
            : (selected
                ? Verdict.Open
                : Verdict.Closed));
    }
    // Snapshots the iterated row's cell keys in cell order before a sweep. Cells minted during the sweep join the
    // next sweep; removals and reordering do not change what this sweep binds.
    private void EachKeys(CompiledRule rule) {
        m_eachKeyScratch.Clear();

        if (rule.Iteration is { } iteration) { iteration.Collect(host: m_host, into: m_eachKeyScratch); return; }

        if (rule.ForEachZones) {
            if (rule.Zones is { } zones) {
                m_eachKeyScratch.AddRange(collection: zones.Indices);
            }

            return;
        }
        if (rule.ForEachBodies) {
            BodyKeys();

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
    // A body sweep binds each live body's index key in ascending index order. The compiler interned every index the
    // body capacity admits, so a live body always resolves; RuleNeeds.Admit already refused a host that is not an
    // ILiveBodies, which the rule's needs name.
    private void BodyKeys() {
        m_bodyIndexScratch.Clear();
        ((ILiveBodies)m_host).CollectLiveBodies(into: m_bodyIndexScratch);

        var keys = m_host.Arena.Keys;

        foreach (var index in m_bodyIndexScratch) {
            if (keys.TryResolveIndex(
                index: index,
                key: out var key
            )) {
                m_eachKeyScratch.Add(item: key);
            }
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
    [Replaces(root: typeof(StateArena))]
    public bool Evaluate(CompiledRule[] rules, RuleLatch latch, ulong stepTicks) {
        _ = Evaluate(
            applied: out var applied,
            latch: latch,
            rules: rules,
            stepTicks: stepTicks
        );

        return applied;
    }
    /// <summary>Evaluates one family of compiled rules in array order and reports what the family did as a whole:
    /// faulted when any evaluation faulted, else refused when any rewound, else fired when any committed, and idle
    /// otherwise.</summary>
    /// <param name="rules">The family, snapshotted by the caller.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether any firing moved the arena.</param>
    /// <returns>The aggregate outcome.</returns>
    [Replaces(root: typeof(StateArena))]
    public RuleOutcome Evaluate(CompiledRule[] rules, RuleLatch latch, ulong stepTicks, out bool applied) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rules);

        applied = false;

        var aggregate = RuleOutcome.Idle;
        var states = latch.States(rules: rules);

        m_host.RefreshFacts();

        for (var index = 0; (index < rules.Length); index++) {
            var outcome = EvaluateRule(
                applied: out var moved,
                bindings: states[index],
                latch: latch,
                rule: rules[index],
                stepTicks: stepTicks
            );

            applied |= moved;
            aggregate = Aggregate(
                left: aggregate,
                right: outcome
            );
        }

        return aggregate;
    }
    // Faulted over refused over fired over idle: the enum's own order is the precedence.
    public static RuleOutcome Aggregate(RuleOutcome left, RuleOutcome right) => ((right > left)
        ? right
        : left);
    /// <summary>Evaluates one compiled rule: once, once per key of the row it iterates, or through the host when the
    /// host claims the rule kind.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any firing moved the arena.</returns>
    [Replaces(root: typeof(StateArena))]
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
    [Replaces(root: typeof(StateArena))]
    public RuleOutcome EvaluateRule(CompiledRule rule, RuleLatch latch, ulong stepTicks, out bool applied) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rule);

        return EvaluateRule(
            applied: out applied,
            bindings: latch.Rule(name: rule.Name),
            latch: latch,
            rule: rule,
            stepTicks: stepTicks
        );
    }

    [Replaces(root: typeof(StateArena))]
    private RuleOutcome EvaluateRule(CompiledRule rule, RuleLatch latch, RuleLatch.RuleState bindings, ulong stepTicks, out bool applied) {
        applied = false;
        bindings.Spent.Visits++;

        var firingsRun = m_firingsRun;

        if (
            (m_owner is { } owner) &&
            owner.TryEvaluateOwn(
            bindings: bindings,
            applied: out var own,
            evaluator: this,
            latch: latch,
            outcome: out var ownOutcome,
            rule: rule,
            stepTicks: stepTicks
        )
        ) {
            applied = own;
            // A firing may change a later pair's inputs and another firing restore them before the next drain.
            m_anyRerun |= (m_firingsRun != firingsRun);

            return ownOutcome;
        }
        if ((rule.ForEachOrdinal < 0) && !rule.ForEachZones && !rule.ForEachBodies && (rule.PoolForEach is null) && (rule.Iteration is null)) {
            return EvaluateOnce(
                applied: out applied,
                binding: LatchKey.None,
                latch: latch,
                rule: rule,
                state: bindings,
                stepTicks: stepTicks
            );
        }

        if (rule.PoolForEach is { } pool) {
            var snapshot = PoolHandles(pool: pool, bindingSlot: rule.PoolBindingSlot);

            latch.BeginSweep();
            var poolAggregate = RuleOutcome.Idle;
            var bindingsSpan = m_host.InstanceBindings;
            var poolFirings = 0;

            foreach (var handle in snapshot) {
                if (
                    (((uint)rule.PoolBindingSlot) >= ((uint)bindingsSpan.Length)) ||
                    !m_host.Arena.TryResolve(handle: handle, position: out _) ||
                    !m_host.Arena.TryPoolKey(slot: handle.Slot, key: out var slotKey)
                ) {
                    continue;
                }

                var poolBinding = new LatchKey(Left: slotKey.Ordinal, Right: -1, LeftGeneration: handle.Generation);

                if ((rule.MaxFirings > 0) && (poolFirings >= rule.MaxFirings)) {
                    latch.Touch(binding: poolBinding, state: bindings);
                    continue;
                }
                bindingsSpan[rule.PoolBindingSlot] = handle;
                var outcome = EvaluateOnce(applied: out var moved, binding: poolBinding, latch: latch, rule: rule, state: bindings, stepTicks: stepTicks);

                applied |= moved;
                if (outcome != RuleOutcome.Idle) {
                    poolFirings++;
                }
                poolAggregate = Aggregate(left: poolAggregate, right: outcome);
            }
            if (((uint)rule.PoolBindingSlot) < ((uint)bindingsSpan.Length)) {
                bindingsSpan[rule.PoolBindingSlot] = default;
            }
            latch.EndSweep(journal: m_host.Arena.Journal, state: bindings);
            return poolAggregate;
        }

        EachKeys(rule: rule);
        latch.BeginSweep();

        var aggregate = RuleOutcome.Idle;
        var firings = 0;

        foreach (var key in m_eachKeyScratch) {
            var eachBinding = new LatchKey(
                Left: key.Ordinal,
                Right: -1,
                LeftGeneration: (rule.Iteration?.Generation(host: m_host, key: key) ?? 0)
            );

            // A sweep that has spent its firings leaves its remaining bindings as they stand, so an open edge waits for
            // the tick that reaches it instead of being closed by the sweep's end.
            if ((rule.MaxFirings > 0) && (firings >= rule.MaxFirings)) {
                latch.Touch(binding: eachBinding, state: bindings);
                continue;
            }
            m_host.BoundEachKey = key;

            var outcome = EvaluateOnce(
                applied: out var moved,
                binding: eachBinding,
                latch: latch,
                rule: rule,
                state: bindings,
                stepTicks: stepTicks
            );

            applied |= moved;
            if (outcome != RuleOutcome.Idle) {
                firings++;
            }
            aggregate = Aggregate(
                left: aggregate,
                right: outcome
            );
        }

        m_host.BoundEachKey = default;
        latch.EndSweep(journal: m_host.Arena.Journal, state: bindings);

        return aggregate;
    }

    /// <summary>Runs one gate-and-fire under the bindings already in place, inside a fault scope of its own. Edge fires
    /// on the crossing alone and re-arms only when the gate closes again; Level fires every tick the gate holds. The
    /// latch entry is per binding, never per rule alone; an entry the enclosing sweep does not touch is closed by the
    /// sweep. An evaluation whose bindings, live rows or gate fault decides nothing: the entry keeps the verdict it
    /// last held, so an Edge rule neither fires nor re-arms on a fault tick.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="state">The rule's latch state (<see cref="RuleLatch.Rule"/>).</param>
    /// <param name="binding">The binding this evaluation runs under.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <param name="applied">Whether the firing moved the arena or emitted through the host.</param>
    /// <returns>What the evaluation did.</returns>
    [Replaces(root: typeof(StateArena))]
    public RuleOutcome EvaluateOnce(CompiledRule rule, RuleLatch latch, RuleLatch.RuleState state, LatchKey binding, ulong stepTicks, out bool applied) {
        m_host.BeginRuleEvaluation();
        try { return EvaluateOnceCore(applied: out applied, binding: binding, latch: latch, rule: rule, state: state, stepTicks: stepTicks); } finally {
            m_host.EndRuleEvaluation();
            m_anyRerun |= m_rerun;
        }
    }

    // What judging one binding decided: the gate's verdict, or that its bindings could not evaluate, and whether a fault
    // decided nothing.
    private readonly record struct Judgement(bool Open, bool Decided, bool Faulted, ulong WakeTick);

    // Runs the judged bindings, the live-row selection and the gate, then, when the open gate fires, the bindings
    // deferred to the firing (CompiledRule.Deferred). Judging fires nothing, so nothing in it can replace the arena;
    // what each step answered is what the judgement decides from. A deferred binding that cannot evaluate decides
    // nothing, exactly as it would have before the gate.
    private Judgement Judge(CompiledRule rule, RuleLatch.RuleState state, RuleLatch.BindingSchedule scheduling, RuleSchedule? schedule, ulong tick, RuleTraceEvaluation? trace, bool firesWhenOpen) {
        var memos = ((schedule is not null) ? Memos(scheduling: scheduling, count: (rule.Locals?.Length ?? 0)) : null);
        var bound = EvaluateBindings(
            deferred: false,
            rule: rule,
            memos: memos,
            localSchedules: state.LocalSchedules,
            tick: tick,
            trace: trace
        );

        if (bound != BindingsResult.Bound) {
            return new Judgement(Decided: false, Faulted: (bound == BindingsResult.Faulted), Open: false, WakeTick: 0UL);
        }

        // Every live row the rule spells must select a row, or this evaluation is simply not for these indices: the
        // gate reads closed, nothing is refused, and the trace names what each spelling selected.
        var rows = RowsSelected(
            rule: rule,
            trace: trace?.Zones
        );
        var verdict = (rows switch {
            Verdict.Open => Decide(
                gate: rule.Gate,
                ruleName: rule.Name,
                trace: trace?.Conjuncts
            ),
            _ => rows,
        });
        var faulted = (verdict == Verdict.Faulted);

        var opens = (verdict switch {
            Verdict.Open => true,
            Verdict.Closed => false,
            Verdict.Faulted => false,
            _ => throw new System.Diagnostics.UnreachableException(),
        });

        if (opens && firesWhenOpen && (rule.Deferred is not null)) {
            var firing = EvaluateBindings(
                deferred: true,
                rule: rule,
                memos: memos,
                localSchedules: state.LocalSchedules,
                tick: tick,
                trace: trace
            );

            if (firing != BindingsResult.Bound) {
                return new Judgement(Decided: false, Faulted: (firing == BindingsResult.Faulted), Open: false, WakeTick: 0UL);
            }
        }

        return new Judgement(
            Decided: !faulted,
            Faulted: faulted,
            Open: opens,
            WakeTick: ((!faulted && (schedule?.Deadline is { } deadline)) ? WakeTick(deadline: deadline, gate: rule.Gate, open: opens, tick: tick) : ulong.MaxValue)
        );
    }
    // The first tick a verdict judged now may no longer hold while every other read holds. A closed verdict: never once
    // the deadline has passed (the tick only grows), else the first tick its comparison holds. An open one (an Edge held
    // across ticks): the next tick for an equality, which stops holding as the tick leaves X, and never for an ordering,
    // which keeps holding. X is read on its own, since the gate may have closed before reading it, and a fault in it is
    // its whole answer; a fault, or a tick side that does not read as the deadline expects, gives tick 0, which no skip
    // trusts.
    private ulong WakeTick(RuleDeadline deadline, GateToken[] gate, bool open, ulong tick) {
        if (open) {
            return (deadline.Exact ? (tick + 1UL) : ulong.MaxValue);
        }

        var token = gate[deadline.Token];
        var frame = new EvaluationFrame(reader: m_host);
        var x = RuleEvaluation.ReadSource(frame: ref frame, kind: token.ValueKind, source: (deadline.TickOnLeft ? token.RightSource : token.LeftSource));
        var now = RuleEvaluation.ReadSource(frame: ref frame, kind: token.ValueKind, source: (deadline.TickOnLeft ? token.LeftSource : token.RightSource));

        if (
            (x.Outcome.Case == FaultCase.Faulted) ||
            !now.Outcome.TryFinite(raw: out var nowRaw) ||
            (nowRaw != unchecked((((long)tick) * deadline.Scale)))
        ) {
            return 0UL;
        }

        var first = deadline.FirstHoldingTick(x: in x);

        return ((tick >= first) ? ulong.MaxValue : first);
    }
    private RuleOutcome EvaluateOnceCore(CompiledRule rule, RuleLatch latch, RuleLatch.RuleState state, LatchKey binding, ulong stepTicks, out bool applied) {
        ArgumentNullException.ThrowIfNull(argument: state);
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rule);

        applied = false;
        m_rerun = true;
        m_evaluations.Increment();
        state.Spent.Evaluations++;

        var tick = m_host.Tick;
        var trace = BeginTrace(
            rule: rule,
            tick: tick
        );
        var entry = state.Binding(binding: binding);
        var existed = entry.Held.HasValue;
        var wasOpen = entry.Held == true;
        var scheduling = entry.Schedule;
        // Scheduling never runs under a trace: a traced evaluation wants every binding and conjunct computed fresh.
        var schedule = ((SchedulingEnabled && (trace is null))
            ? Schedule(rule: rule, state: state)
            : null
        );
        var level = (rule.Mode != ActionTriggerMode.Edge);
        // A Level gate that held fires again, so the verdict it stands on is its whole firing; every other verdict
        // fires nothing while it stands, and is the judgement alone.
        var refires = (wasOpen && level);
        var standing = ((schedule is null) ? null : (refires ? state.FiringSchedule : schedule));
        var stands = (
            existed &&
            (standing is { Volatile: false }) &&
            ReferenceEquals(objA: scheduling.Owner, objB: standing) &&
            (tick < scheduling.WakeTick) &&
            (!refires || (scheduling.StepTicks == stepTicks)) &&
            VersionsMatch(
            cached: scheduling.Versions,
            schedule: standing
        )
        );
        var held = (stands && refires && QuiescenceChecked);

        if (stands && !held) {
            // Nothing the verdict stands on has changed since it was captured, it stands on nothing a version cannot
            // see through, and its deadline (if it has one) is still ahead. A closed gate or a held Edge fires
            // nothing, so bindings and gate need not run; a Level firing that changed nothing would read what it read
            // and write what is already there, so it need not fire, and answers what its firing answered.
            m_skips.Increment();
            state.Spent.Skips++;
            if (refires) {
                m_settled.Increment();
                state.Spent.Settled++;
            }
            latch.Touch(schedule: scheduling);
            EndTrace(entry: trace);
            m_rerun = false;
            m_minWake = Math.Min(val1: m_minWake, val2: scheduling.WakeTick);

            return (refires ? RuleOutcome.Fired : RuleOutcome.Idle);
        }

        var judged = Judge(
            firesWhenOpen: ((rule.Mode != ActionTriggerMode.Edge) || !wasOpen),
            rule: rule,
            schedule: schedule,
            scheduling: scheduling,
            state: state,
            tick: tick,
            trace: trace
        );

        if (!judged.Decided) {
            // A binding that could not evaluate refused (and was reported) this tick, so the next tick judges it again.
            m_rerun = true;
            NoteUnsettled(held: held, rule: rule, standing: standing, tick: tick);

            return HoldOnFault(faulted: judged.Faulted, latch: latch, scheduling: scheduling, trace: trace);
        }

        var open = judged.Open;

        latch.Touch(schedule: scheduling);

        if (
            (schedule is { } reads) &&
            (!open || !level)
        ) {
            // A verdict that fires nothing while its reads hold — closed, or an Edge that is held once it crosses —
            // remembers what its reads looked like, so an unchanged read next time skips. A crossing's own firing may
            // move what it read, and then the versions differ and the next tick judges again.
            CaptureVersions(
                cache: ref scheduling.Versions,
                schedule: reads
            );
            scheduling.Owner = reads;
            scheduling.WakeTick = judged.WakeTick;
            m_minWake = Math.Min(val1: m_minWake, val2: judged.WakeTick);
        }

        // A Level gate that holds fires again next tick, unless its firing below proves it changes nothing.
        m_rerun = ((schedule is not { Volatile: false }) || (open && level));

        var fires = (open && (level || !wasOpen));

        if (trace is not null) {
            trace.EdgeHeld = (open && !fires);
            trace.GateOpen = open;
        }

        var changed = (!existed || (wasOpen != open));

        if (!changed && !fires) {
            EndTrace(entry: trace);
            NoteUnsettled(held: held, rule: rule, standing: standing, tick: tick);

            return RuleOutcome.Idle;
        }

        // A Level firing over a bit that already reads open writes no bookkeeping, so the firing is all its scope
        // holds: the one a versioned firing schedule can take as settled.
        var settles = ((fires && !changed && (schedule is not null) && (state.FiringSchedule is { Volatile: false } firingSchedule))
            ? firingSchedule
            : null
        );
        var settled = false;
        var outcome = ((fires && (rule.Effects is [RewindGroupEffect]))
            ? FireRewind(
                applied: out applied,
                binding: binding,
                latch: latch,
                open: open,
                rule: rule,
                stepTicks: stepTicks,
                tick: tick
            )
            : FireProposal(
                applied: out applied,
                binding: binding,
                changed: changed,
                fires: fires,
                latch: latch,
                open: open,
                rule: rule,
                settled: out settled,
                settles: settles,
                state: state,
                stepTicks: stepTicks,
                tick: tick
            ));

        if (settled) {
            // The firing moved no row, queued no arm, refused nothing and left the host as it was, and everything it
            // read or wrote is named and versioned: fired again over the same versions it reads the same values and
            // writes what is already there. The verdict stands until a version it names moves.
            CaptureVersions(
                cache: ref scheduling.Versions,
                schedule: settles!
            );
            scheduling.Owner = settles;
            scheduling.StepTicks = stepTicks;
            scheduling.WakeTick = judged.WakeTick;
            m_minWake = Math.Min(val1: m_minWake, val2: judged.WakeTick);
            // Under the check every such firing is fired again, so a due list keeps visiting it.
            m_rerun = QuiescenceChecked;
        } else if (fires && level) {
            scheduling.Owner = null;
        }
        NoteUnsettled(held: (held && !settled), rule: rule, standing: standing, tick: tick);

        if (outcome is RuleOutcome.Faulted or RuleOutcome.Refused) {
            // The scope rewound the edge bit, so the verdict the schedule cached against it is stale, and the next tick
            // judges the binding again.
            scheduling.Owner = null;
            m_rerun = true;
        }
        if (outcome == RuleOutcome.Fired) {
            m_firings.Increment();
            state.Spent.Firings++;

            if (rule.Effects is [RewindGroupEffect]) {
                latch.InvalidateScheduler();
            }
        }

        EndTrace(entry: trace);

        return outcome;
    }
    // One evaluation's proposal: the edge bit and the firing's effects written into one journal scope, which the one
    // door (Commit) then commits or rewinds together.
    // `settles` is the firing schedule a firing that changes nothing may stand on, or null; `settled` answers whether
    // this one did. The proof is the firing's own: its scope is the outermost one, so its commit settles every row
    // version; no version moved across it (StateArena.MoveSequence moves exactly when one does); every row it wrote,
    // changed or not, is one the schedule names (the journal's own entries, so no effect's declared write set is
    // trusted for the rows it did write); it queued no arm, reported no refusal, and the host's commit closed the
    // arena's scope and nothing else (IEffectHost.CommitStateTransaction).
    private RuleOutcome FireProposal(CompiledRule rule, RuleLatch.RuleState state, LatchKey binding, RuleLatch latch, bool changed, bool fires, bool open, ulong tick, ulong stepTicks, out bool applied, RuleSchedule? settles, out bool settled) {
        applied = false;
        settled = false;

        var firing = new EffectFiring(
            EngineTick: m_host.EngineTick,
            RuleName: rule.Name,
            StepTicks: stepTicks,
            Tick: tick
        );
        // Held as an identity alone: after the commit, which may install another arena, it is compared with the live
        // arena's journal and never read through.
        object opened = m_host.Arena.Journal;
        var moves = m_host.Arena.MoveSequence;
        var refusals = m_refusalSerial;
        var outermost = !m_host.Arena.Journal.ScopeOpen;
        var mark = m_host.BeginStateTransaction();
        var queued = m_deferred.Count;
        Step step;

        try {
            if (changed) {
                latch.SetHeld(
                    binding: binding,
                    held: open,
                    journal: m_host.Arena.Journal,
                    state: state
                );
            }

            step = (fires
                ? ProposeEffects(
                    applied: ref applied,
                    effects: rule.Effects,
                    firing: in firing,
                    queued: queued,
                    ruleName: rule.Name
                )
                : Step.Done);
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

        var quiet = (
            (settles is not null) &&
            outermost &&
            (step == Step.Done) &&
            (m_deferred.Count == queued) &&
            (m_refusalSerial == refusals) &&
            WritesWithin(arena: m_host.Arena, schedule: settles)
        );

        m_commitArenaOnly = false;

        var outcome = Commit(
            applied: ref applied,
            firing: in firing,
            proposal: new Proposal(
                Decision: ((step == Step.Done)
                    ? (fires
                        ? FiringDecision.Fires
                        : (open
                            ? FiringDecision.Held
                            : FiringDecision.Closed))
                    : ((step == Step.Faulted)
                        ? FiringDecision.Faulted
                        : FiringDecision.Refused)),
                Mark: mark,
                Queued: queued
            )
        );

        settled = (
            quiet &&
            (outcome == RuleOutcome.Fired) &&
            m_commitArenaOnly &&
            ReferenceEquals(objA: m_host.Arena.Journal, objB: opened) &&
            (m_host.Arena.MoveSequence == moves)
        );

        return outcome;
    }
    // Whether every row the open scopes wrote, whether or not a value moved, is a row the schedule names.
    private bool WritesWithin(StateArena arena, RuleSchedule schedule) {
        if (m_writtenFlags.Length < arena.Layout.RowCount) {
            m_writtenFlags = new bool[arena.Layout.RowCount];
        }

        m_writtenRows.Clear();
        arena.FlagOpenRows(rows: m_writtenFlags, written: m_writtenRows);

        var within = true;

        foreach (var row in m_writtenRows) {
            m_writtenFlags[row] = false;
            within &= (Array.BinarySearch(array: schedule.Rows, value: row) >= 0);
        }

        return within;
    }
    // Records the first Level firing the check fired in place of a skip that did not settle again.
    private void NoteUnsettled(bool held, CompiledRule rule, RuleSchedule? standing, ulong tick) {
        if (held && (StaleQuiescence is null)) {
            StaleQuiescence = $"rule '{rule.Name}' at tick {tick}: a Level firing its schedule answered for did not settle when fired again ({standing?.Describe(reader: m_host)})";
        }
    }
    // A rule whose whole effect is rewinding its group restores the world to a turn boundary the undo arena holds, which
    // no scope can enclose, so the edge bit cannot ride a scope: it is written after the restore, and only when the
    // restore landed. A rewind that refuses has not crossed its Edge, so the next tick tries again.
    private RuleOutcome FireRewind(CompiledRule rule, RuleLatch latch, LatchKey binding, bool open, ulong tick, ulong stepTicks, out bool applied) {
        var outcome = FireRule(
            applied: out applied,
            rule: rule,
            stepTicks: stepTicks,
            tick: tick
        );

        if (outcome == RuleOutcome.Fired) {
            latch.Restore(
                binding: binding,
                held: open,
                journal: m_host.Arena.Journal,
                name: rule.Name
            );
        }

        return outcome;
    }
    // A fault decided nothing. A binding that merely could not evaluate (an absence) leaves its entry for the sweep to
    // close, as a closed gate would; one that faulted touches it, so the sweep keeps the verdict the entry last held.
    private RuleOutcome HoldOnFault(bool faulted, RuleLatch latch, RuleLatch.BindingSchedule scheduling, RuleTraceEvaluation? trace) {
        if (faulted) {
            latch.Touch(schedule: scheduling);
        }
        EndTrace(entry: trace);

        return (faulted
            ? RuleOutcome.Faulted
            : RuleOutcome.Idle);
    }

    /// <summary>Decides a compiled gate for the evaluation in flight. A conjunct that overflows, leaves a function's
    /// domain or reads a fact with no number leaves the gate <see cref="Verdict.Faulted"/>: it decided nothing, so a
    /// rule keeps its last verdict, a decision its choice, and an <c>if</c> effect's condition runs neither branch and
    /// refuses its firing. The fault is reported against the rule in flight.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="ruleName">The rule the gate belongs to, for the refusal ledger.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    /// <returns>What the gate decided.</returns>
    public Verdict Decide(GateToken[] gate, string ruleName, List<string>? trace = null) {
        var frame = new EvaluationFrame(reader: m_host);
        var verdict = RuleEvaluation.Decide(
            frame: ref frame,
            gate: gate,
            trace: trace
        );

        if (verdict == Verdict.Faulted) {
            ReportRefusal(
                detail: $"{RuleEvaluation.GateFaultReason}; the gate decided nothing",
                effect: "gate",
                refusal: RuleEffectRefusal.Arithmetic,
                ruleName: ruleName,
                tick: m_host.Tick
            );
        }

        return verdict;
    }
    /// <summary>Populates locals for a host-owned evaluation under its current binding before any gate or score
    /// reads them. Each value is evaluated afresh so timers cannot retain another binding's locals.</summary>
    /// <param name="rule">The compiled rule whose locals are evaluated in declaration order.</param>
    /// <returns>What the bindings answered; a binding that did not evaluate is reported and closes this
    /// evaluation.</returns>
    public BindingsResult EvaluateBindings(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var judged = EvaluateBindings(deferred: false, rule: rule, memos: null, localSchedules: [], tick: m_host.Tick, trace: null);

        return (((judged != BindingsResult.Bound) || (rule.Deferred is null))
            ? judged
            : EvaluateBindings(deferred: true, rule: rule, memos: null, localSchedules: [], tick: m_host.Tick, trace: null));
    }

    // One binding's memo array, sized to the rule's current local count; a stale array (a recompile changed the
    // count) is replaced, discarding its cached values.
    private static RuleLatch.BindingMemo?[] Memos(RuleLatch.BindingSchedule scheduling, int count) {
        if ((scheduling.Memos is not { } memos) || (memos.Length != count)) {
            scheduling.Memos = memos = new RuleLatch.BindingMemo?[count];
        }

        return memos;
    }
    // One binding evaluated into its slot of the host's bound values. One that cannot evaluate is reported once per
    // category like any effect's arithmetic refusal: an absence closes the evaluation, a fault decides nothing.
    private BindingsResult EvaluateLocal(CompiledRuleLocal bound, int ordinal, string ruleName, ulong tick, out long value) {
        var frame = new EvaluationFrame(reader: m_host);
        var evaluated = RuleExpressions.Evaluate(
            frame: ref frame,
            kind: bound.CarrierKind,
            program: bound.Expression
        );

        if (!evaluated.TryFinite(raw: out value)) {
            ReportRefusal(
                detail: RuleEvaluation.DescribeOutcome(outcome: evaluated),
                effect: $"binding '{bound.Name}'",
                refusal: RuleEffectRefusal.Arithmetic,
                ruleName: ruleName,
                tick: tick
            );

            return ((evaluated.Case == FaultCase.Faulted)
                ? BindingsResult.Faulted
                : BindingsResult.Absent);
        }

        m_host.Locals[ordinal] = value;

        return BindingsResult.Bound;
    }
    // Bound values in declared order, each visible to the ones after it and to the gate and effects: the judged ones
    // (deferred false), or the ones deferred to the firing (deferred true), which read only judged ones and earlier
    // deferred ones. A binding that cannot evaluate closes the gate for this evaluation and is reported once per
    // category like any effect's arithmetic refusal. With memos, localSchedules holds each local's read schedule.
    private BindingsResult EvaluateBindings(CompiledRule rule, bool deferred, RuleLatch.BindingMemo?[]? memos, RuleSchedule[] localSchedules, ulong tick, RuleTraceEvaluation? trace) {
        var declared = (rule.Locals ?? []);

        if (declared.Length == 0) {
            return BindingsResult.Bound;
        }

        for (var ordinal = 0; (ordinal < declared.Length); ordinal++) {
            if (rule.IsDeferred(ordinal: ordinal) != deferred) {
                continue;
            }

            var bound = declared[ordinal];

            if (memos is { } cached) {
                var bindingSchedule = localSchedules[ordinal];

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

            var local = EvaluateLocal(
                bound: bound,
                ordinal: ordinal,
                ruleName: rule.Name,
                tick: tick,
                value: out var value
            );

            if (local != BindingsResult.Bound) {
                trace?.Locals.Add(item: $"{bound.Name}=refused");

                return local;
            }

            if (memos is { } cache) {
                var memo = (cache[ordinal] ??= new RuleLatch.BindingMemo());
                var bindingSchedule = localSchedules[ordinal];
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

        return BindingsResult.Bound;
    }
}
