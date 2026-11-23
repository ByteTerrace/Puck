namespace Puck.State.Rules;

public sealed partial class RuleEvaluator {
    private readonly RuleLatch[] m_additionalFamilies;
    private readonly RuleDueList?[] m_dueLists;
    private readonly List<FactRead> m_movedFacts = [];

    // A drain consumes the host feed. Deliver it to every installed pass, including families already visited this tick.
    internal void DrainFactMoves() {
        m_movedFacts.Clear();
        var all = m_host.DrainFactMoves(into: m_movedFacts);
        if (!all && (m_movedFacts.Count == 0)) { return; }
        foreach (var family in m_dueLists) { family?.ApplyFactMoves(all: all, moves: m_movedFacts); }
    }

    private readonly List<int> m_dueLive = [];

    private Puck.Abstractions.Counting.WorkCount m_due;
    private Puck.Abstractions.Counting.WorkCount m_elided;

    /// <summary>Evaluates one family of compiled rules in array order, visiting only the rules that are due: a rule
    /// whose schedule cannot name what it reads, one whose last verdict must be judged again (a Level gate that
    /// held, a refusal), and one a row or host fact it reads moved since its last verdict. The arena's moved-row feed
    /// and the host's moved-fact feed push the readers onto the list as they move, so a firing earlier in document
    /// order marks a later reader due in the same tick, and an earlier reader the next tick, exactly where the
    /// version check would have found the move. A rule that is not due is not visited: its in-place evaluation
    /// would reproduce its last verdict bit for bit. A keyed sweep (a keyed row's, the live bodies') due for some of
    /// its bindings judges only theirs.</summary>
    /// <param name="rules">The family, snapshotted by the caller.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any firing moved the arena.</returns>
    /// <remarks>With <see cref="SchedulingEnabled"/> off it is <see cref="Evaluate(CompiledRule[], RuleLatch, ulong)"/>: every rule, every tick.</remarks>
    [Replaces(root: typeof(StateArena))]
    public bool EvaluateDue(CompiledRule[] rules, RuleLatch latch, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var slot = Array.IndexOf(m_additionalFamilies, latch) + 1;
        if (!SchedulingEnabled) {
            m_dueLists[slot] = null;

            return Evaluate(latch: latch, rules: rules, stepTicks: stepTicks);
        }

        m_host.RefreshFacts();

        var list = m_dueLists[slot];

        if ((list is null) || !list.Covers(epoch: m_host.FactScheduleEpoch, latch: latch, rules: rules)) {
            list?.SettleElided();
            m_dueLists[slot] = list = new RuleDueList(evaluator: this, latch: latch, rules: rules);
        }

        MarkTraced(list: list);
        list.Wake(tick: m_host.Tick);
        list.Drain();

        var applied = false;
        var visited = 0;

        for (var index = list.NextDue(from: 0); (index >= 0); index = list.NextDue(from: (index + 1))) {
            list.BeginVisit(index: index);
            visited++;
            m_due.Increment();
            m_anyRerun = false;
            m_minWake = ulong.MaxValue;

            var whole = true;
            var moved = (list.KindOf(index: index) switch {
                RuleDueList.Visit.BodySweep => SweepBodies(index: index, list: list, stepTicks: stepTicks, whole: out whole),
                RuleDueList.Visit.RowSweep => SweepRow(index: index, list: list, stepTicks: stepTicks, whole: out whole),
                _ => (EvaluateRule(applied: out var ruleMoved, bindings: list.States[index], latch: latch, rule: rules[index], stepTicks: stepTicks) != RuleOutcome.Idle) | ruleMoved,
            });

            applied |= moved;
            list.Settle(index: index, rerun: m_anyRerun);
            list.RecordWake(index: index, wake: m_minWake, whole: whole);
            list.Drain();
        }

        var elided = list.CountElided();

        m_elided.Add(amount: elided);

        return applied;
    }

    // A traced rule is judged in full on every tick its trace wants evaluations: a trace wants every binding
    // computed fresh, and a rule that is not due would capture nothing.
    private void MarkTraced(RuleDueList list) {
        if (m_traceAll && (m_traceCaptured.Count < m_traceWanted)) {
            list.MarkAll();

            return;
        }
        if ((m_traceRule is { } traced) && (m_traceCaptured.Count < m_traceWanted)) {
            list.MarkNamed(name: traced);
        }
    }
    // One visit of a body sweep the list holds per body: every live body when the rule is due in full (a row or the
    // whole roster it reads moved, or its list was just built), otherwise only the bodies due. Each binding still
    // passes the version door, so a due body whose own facts did not move keeps its verdict.
    private bool SweepBodies(int index, RuleDueList list, ulong stepTicks, out bool whole) {
        var rule = list.Rules[index];
        var state = list.States[index];
        var latch = list.Latch;
        var keys = m_host.Arena.Keys;
        var applied = false;
        var full = list.TakeFull(index: index);

        whole = full;

        state.Spent.Visits++;
        state.Spent.Sweeps++;
        m_dueLive.Clear();
        ((ILiveBodies)m_host).CollectLiveBodies(into: m_dueLive);
        list.SnapshotLive(index: index, live: m_dueLive);
        latch.BeginSweep();

        foreach (var body in m_dueLive) {
            if (!full && !list.IsFull(index: index) && !list.IsBodyDue(body: body, index: index)) {
                continue;
            }

            list.ClearBinding(binding: body, index: index);

            if (!keys.TryResolveIndex(index: body, key: out var key)) {
                continue;
            }

            var binding = new LatchKey(Left: key.Ordinal, Right: -1);

            m_host.BoundEachKey = key;

            var firingsRun = m_firingsRun;

            var outcome = EvaluateOnce(
                applied: out var moved,
                binding: binding,
                latch: latch,
                rule: rule,
                state: state,
                stepTicks: stepTicks
            );

            applied |= moved;
            if (m_rerun) {
                list.MarkBindingNext(binding: body, index: index);
            }
            if (!full && !latch.TouchedInSweep(binding: binding, state: state)) {
                // A binding that could not evaluate is closed exactly as a full sweep's end closes it.
                latch.Forget(binding: binding, journal: m_host.Arena.Journal, state: state);
            }
            if ((outcome == RuleOutcome.Fired) && (m_firingsRun != firingsRun)) {
                list.Drain();
            }
        }

        m_host.BoundEachKey = default;
        if (full) {
            latch.EndSweep(journal: m_host.Arena.Journal, state: state);
        }
        list.ForgetUnswept(index: index, journal: m_host.Arena.Journal, keys: keys, latch: latch, state: state, swept: full);

        return applied;
    }
    // One visit of a keyed row's sweep the list holds per key. Due in full (a cell every binding reads, the swept
    // row's structure or a deadline moved, or its list was just built), it snapshots the row's keys and judges each
    // in cell order, as an in-place sweep does. Otherwise it judges only the due keys, in the same order, over the
    // snapshot its last full visit took: the row's structure has not moved since (RuleDueList.SnapshotStands), so
    // the snapshot is the one an in-place sweep would take now, and it costs the due keys rather than the row. A
    // firing's moves are drained at once, so a key later in the order that the firing reaches is judged this tick
    // and an earlier one next tick; when a firing makes the whole rule due, every later key is judged, over the same
    // snapshot, since a sweep binds the keys the row held as it began.
    private bool SweepRow(int index, RuleDueList list, ulong stepTicks, out bool whole) {
        var rule = list.Rules[index];
        var state = list.States[index];
        var latch = list.Latch;
        var snapshot = list.SnapshotOf(index: index);
        var applied = false;
        var structure = m_host.Arena.RowStructureVersion(rowOrdinal: rule.ForEachOrdinal);
        var full = list.TakeFull(force: !list.SnapshotStands(index: index, structure: structure), index: index);

        whole = full;
        state.Spent.Visits++;
        if (full) {
            state.Spent.Sweeps++;
            if (snapshot.Structure != structure) {
                EachKeys(rule: rule);
                snapshot.Take(keys: m_eachKeyScratch, structure: structure);
            }
            list.NoteSwept(index: index, structure: structure);
        }
        latch.BeginSweep();

        // Whether every key from here on is judged: from the start of a full sweep, or once a firing made the rule
        // due in full.
        var every = full;

        for (var position = 0; (position < snapshot.Count); position++) {
            every |= list.IsFull(index: index);
            if (!every) {
                position = list.NextBindingDue(from: position, index: index);
                if ((position < 0) || (position >= snapshot.Count)) {
                    break;
                }
            }
            list.ClearBinding(binding: position, index: index);

            var key = snapshot.Keys[position];
            var binding = new LatchKey(Left: key.Ordinal, Right: -1);

            m_host.BoundEachKey = key;

            var firingsRun = m_firingsRun;
            var outcome = EvaluateOnce(
                applied: out var moved,
                binding: binding,
                latch: latch,
                rule: rule,
                state: state,
                stepTicks: stepTicks
            );

            applied |= moved;
            if (m_rerun) {
                list.MarkBindingNext(binding: position, index: index);
            }
            if (!full && !latch.TouchedInSweep(binding: binding, state: state)) {
                // A binding that could not evaluate is closed exactly as a full sweep's end closes it.
                latch.Forget(binding: binding, journal: m_host.Arena.Journal, state: state);
            }
            if ((outcome == RuleOutcome.Fired) && (m_firingsRun != firingsRun)) {
                list.Drain();
            }
        }

        m_host.BoundEachKey = default;
        if (full) {
            latch.EndSweep(journal: m_host.Arena.Journal, state: state);
        }

        return applied;
    }

    internal bool Owns(CompiledRule rule) => ((m_owner is { } owner) && owner.Owns(rule: rule));

    internal RuleSchedule ScheduleOf(CompiledRule rule, RuleLatch.RuleState state) => Schedule(rule: rule, state: state);

    /// <summary>Brings every rule's elided count (<see cref="RuleLatch.RuleSpent.Elided"/>) up to date. A due list
    /// counts a rule's elided ticks when the rule is next visited, so a reader of the spent counts calls this
    /// first.</summary>
    public void SettleSpent() {
        foreach (var list in m_dueLists) {
            list?.SettleElided();
        }
    }
}
