namespace Puck.State.Rules;

public sealed partial class RuleEvaluator {
    private RuleDueList? m_dueList;

    private readonly List<int> m_dueLive = [];

    private Puck.Abstractions.Counting.WorkCount m_due;
    private Puck.Abstractions.Counting.WorkCount m_elided;

    /// <summary>Evaluates one family of compiled rules in array order, visiting only the rules that are due: a rule
    /// whose schedule cannot name what it reads, one whose last verdict must be judged again (a Level gate that
    /// held, a refusal), and one a row or host fact it reads moved since its last verdict. The arena's moved-row feed
    /// and the host's moved-fact feed push the readers onto the list as they move, so a firing earlier in document
    /// order marks a later reader due in the same tick, and an earlier reader the next tick, exactly where the
    /// version check would have found the move. A rule that is not due is not visited: its in-place evaluation
    /// would reproduce its last verdict bit for bit. A body sweep due for some bodies judges only theirs.</summary>
    /// <param name="rules">The family, snapshotted by the caller.</param>
    /// <param name="latch">The family's edge latch.</param>
    /// <param name="stepTicks">How many engine ticks the simulation step spans.</param>
    /// <returns><see langword="true"/> when any firing moved the arena.</returns>
    /// <remarks>With <see cref="SchedulingEnabled"/> off it is <see cref="Evaluate(CompiledRule[], RuleLatch, ulong)"/>: every rule, every tick.</remarks>
    [Replaces(root: typeof(StateArena))]
    public bool EvaluateDue(CompiledRule[] rules, RuleLatch latch, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: latch);
        ArgumentNullException.ThrowIfNull(argument: rules);

        if (!SchedulingEnabled) {
            m_dueList = null;

            return Evaluate(latch: latch, rules: rules, stepTicks: stepTicks);
        }

        m_host.RefreshFacts();

        var list = m_dueList;

        if ((list is null) || !list.Covers(epoch: m_host.FactScheduleEpoch, latch: latch, rules: rules)) {
            m_dueList = list = new RuleDueList(evaluator: this, latch: latch, rules: rules);
        }

        MarkTraced(list: list);
        list.Wake(tick: m_host.Tick);
        list.Drain();

        var applied = false;
        var visited = 0;

        for (var index = list.NextDue(from: 0); (index >= 0); index = list.NextDue(from: (index + 1))) {
            list.Visit(index: index);
            visited++;
            m_due.Increment();
            m_anyRerun = false;
            m_minWake = ulong.MaxValue;

            var whole = !list.IsBodySweep(index: index);
            var moved = (whole
                ? (EvaluateRule(applied: out var ruleMoved, bindings: list.States[index], latch: latch, rule: rules[index], stepTicks: stepTicks) != RuleOutcome.Idle) | ruleMoved
                : SweepBodies(index: index, list: list, stepTicks: stepTicks, whole: out whole));

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
        m_dueLive.Clear();
        ((ILiveBodies)m_host).CollectLiveBodies(into: m_dueLive);
        list.SnapshotLive(index: index, live: m_dueLive);
        latch.BeginSweep();

        foreach (var body in m_dueLive) {
            if (!full && !list.IsFull(index: index) && !list.IsBodyDue(body: body, index: index)) {
                continue;
            }

            list.ClearBody(body: body, index: index);

            if (!RuleReads.TryIndexKey(index: body, key: out var key, keys: keys)) {
                continue;
            }

            var binding = new LatchKey(Left: key.Ordinal, Right: -1);

            m_host.BoundEachKey = key;

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
                list.MarkBodyNext(body: body, index: index);
            }
            if (!full && !latch.TouchedInSweep(binding: binding, state: state)) {
                // A binding that could not evaluate is closed exactly as a full sweep's end closes it.
                latch.Forget(binding: binding, state: state);
            }
            if (outcome == RuleOutcome.Fired) {
                list.Drain();
            }
        }

        m_host.BoundEachKey = default;
        if (full) {
            latch.EndSweep(journal: m_host.Arena.Journal, state: state);
        }
        list.ForgetUnswept(index: index, keys: keys, latch: latch, state: state, swept: full);

        return applied;
    }

    internal RuleSchedule ScheduleOf(CompiledRule rule, RuleLatch.RuleState state) => Schedule(rule: rule, state: state);
    internal bool Owns(CompiledRule rule) => ((m_owner is { } owner) && owner.Owns(rule: rule));
}
