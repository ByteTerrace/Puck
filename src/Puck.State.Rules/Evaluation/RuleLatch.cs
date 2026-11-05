using System.Runtime.InteropServices;

using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The evaluation binding a latch entry belongs to: an iteration key's interned ordinal or a host-bound
/// left participant in <see cref="Left"/> with <see cref="Right"/> -1, a host-bound pair in both, and
/// <see cref="None"/> for a rule evaluated once.</summary>
/// <param name="Left">The left index, or -1.</param>
/// <param name="Right">The right index, or -1.</param>
/// <param name="LeftGeneration">The left pool lifetime generation, or 0 for ordinary bindings.</param>
/// <param name="RightGeneration">The right pool lifetime generation, or 0 for ordinary bindings.</param>
public readonly record struct LatchKey(int Left, int Right, long LeftGeneration = 0L, long RightGeneration = 0L) {
    /// <summary>The binding of a rule evaluated once per tick.</summary>
    public static readonly LatchKey None = new(
        Left: -1,
        LeftGeneration: 0L,
        Right: -1,
        RightGeneration: 0L
    );
}
/// <summary>One rule family's edge latch — per rule name and per binding, whether the gate held at the last
/// evaluation — kept outside the compiled array because a rule's own effect recompiles it. A bound entry not touched
/// between <see cref="BeginSweep"/> and <see cref="EndSweep"/> is closed: that is how a pair that left range, or a
/// carrier that despawned, re-arms and is forgotten. The latch is simulation state: it hashes and checkpoints.</summary>
public sealed class RuleLatch {
    /// <summary>One binding's memoized value and the row versions it was computed against.</summary>
    public sealed class BindingMemo {
        /// <summary>Gets or sets the schedule instance <see cref="Versions"/> was captured against. A rule
        /// recompiled under the same name mints a new schedule, so an entry whose owner no longer matches the
        /// caller's current schedule names a stale cache rather than a coincidental match on row count alone.</summary>
        public object? Owner { get; set; }
        /// <summary>Gets or sets the binding's cached raw value.</summary>
        public long Value { get; set; }
        /// <summary>Gets or sets the row versions observed when <see cref="Value"/> was computed.</summary>
        public ulong[] Versions { get; set; } = [];
    }

    /// <summary>What the evaluator spent on one rule since its latch state was minted: deterministic counts, never
    /// hashed, flattened or restored, read by the measured sheet.</summary>
    public sealed class RuleSpent {
        internal RuleSpent() { }

        /// <summary>Gets how many times the evaluator visited the rule: one per sweep, or one per evaluation of a rule
        /// evaluated once.</summary>
        public long Visits { get; internal set; }
        /// <summary>Gets how many bindings the evaluator was asked to judge, skipped ones included.</summary>
        public long Evaluations { get; internal set; }
        /// <summary>Gets how many of <see cref="Evaluations"/> the schedule answered without running bindings or gate.</summary>
        public long Skips { get; internal set; }
        /// <summary>Gets how many firings committed and moved the arena.</summary>
        public long Firings { get; internal set; }
        /// <summary>Gets how many ticks a due list left the rule unvisited, because nothing it reads had moved and its
        /// last verdict asked for nothing.</summary>
        public long Elided { get; internal set; }
    }

    /// <summary>One rule name's share of the latch: its edge bits, which are simulation state, and the scheduler's
    /// per-binding caches beside them, which are not and are never hashed, flattened or restored. An evaluator
    /// resolves it once per rule and tick, so evaluating a binding costs one lookup into each table.</summary>
    public sealed class RuleState {
        internal RuleState() { }

        /// <summary>Gets whether the gate held at the last evaluation, per binding: the hashed and checkpointed half.</summary>
        public Dictionary<LatchKey, bool> Held { get; } = [];

        /// <summary>Gets what the evaluator spent on the rule.</summary>
        public RuleSpent Spent { get; } = new();

        internal readonly Dictionary<LatchKey, BindingSchedule> Scheduling = [];

        // The binding of a rule evaluated once per tick, kept out of the table it would be the only entry of.
        internal BindingSchedule? Once;

        // The evaluator and compiled rule the read schedules below were derived for: a schedule is a function of
        // both, so either changing derives them again.
        internal object? ScheduleEvaluator;
        internal object? ScheduleRule;
        internal RuleSchedule? Schedule;
        internal RuleSchedule[] LocalSchedules = [];
        internal int ScheduleEpoch;

        internal BindingSchedule Binding(LatchKey binding) {
            if (binding.Equals(other: LatchKey.None)) {
                return (Once ??= new BindingSchedule());
            }

            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: Scheduling, exists: out _, key: binding);

            return (entry ??= new BindingSchedule());
        }
    }

    // The scheduler's caches for one binding: the sweep that last touched it, the row versions its gate observed when
    // it last closed under GateOwner's schedule, and its memoized local values.
    internal sealed class BindingSchedule {
        public ulong Touched;
        public object? GateOwner;
        public ulong[] GateVersions = [];
        public BindingMemo?[]? Memos;
    }

    private readonly Dictionary<string, RuleState> m_byRule = new(comparer: StringComparer.Ordinal);

    // Starts above zero, the sweep a fresh or reset binding schedule reads as last touched in, so it reads untouched.
    private ulong m_sweep = 1UL;

    private readonly List<HashEntry> m_hashScratch = [];

    /// <summary>Gets how many ticks the host counted through <see cref="CountTick"/> since the latch was created or
    /// last cleared: the window every rule's <see cref="RuleState.Spent"/> covers.</summary>
    public long SpentTicks { get; private set; }

    /// <summary>Counts one tick of the family's evaluation into <see cref="SpentTicks"/>.</summary>
    public void CountTick() => SpentTicks++;
    /// <summary>Returns one rule's state when the latch holds it.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="state">The state, when held.</param>
    /// <returns><see langword="true"/> when the latch holds state for the name.</returns>
    public bool TryGetRule(string name, out RuleState state) {
        if (m_byRule.TryGetValue(key: name, value: out var held)) {
            state = held;

            return true;
        }

        state = null!;

        return false;
    }
    /// <summary>Gets how many bound entries the latch holds across every rule.</summary>
    public int Count {
        get {
            var count = 0;

            foreach (var state in m_byRule.Values) {
                count += state.Held.Count;
            }

            return count;
        }
    }

    /// <summary>Folds the latch into a state hash, in compiled order with bindings sorted, so two hosts holding the
    /// same latch hash the same regardless of insertion order. A key-bound entry folds its key's name, never its
    /// interned ordinal: an ordinal is arena layout, and a restored arena interns the same names in another order.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="compiled">The family's compiled rules.</param>
    /// <param name="keyNames">The arena's interned key names by ordinal, for a family whose bindings are iteration keys
    /// (<see cref="LatchKey.Left"/> an interned ordinal); <see langword="null"/> for a family bound to participant
    /// indices, which are simulation identities and fold as themselves.</param>
    public void AppendStateHash(ref Fnv1aHash hash, CompiledRule[] compiled, IReadOnlyList<CellName>? keyNames = null) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        hash.Add(value: ((uint)compiled.Length));

        foreach (var rule in compiled) {
            hash.Add(value: Fnv1aHash.Compute(values: rule.Name.AsSpan()));

            if (!m_byRule.TryGetValue(
                key: rule.Name,
                value: out var state
            )) {
                hash.Add(value: 0U);

                continue;
            }

            m_hashScratch.Clear();
            foreach (var (binding, held) in state.Held) {
                m_hashScratch.Add(item: new HashEntry(
                    Binding: binding,
                    Held: held,
                    Left: LeftIdentity(
                        binding: binding,
                        keyNames: keyNames
                    )
                ));
            }

            var ordered = CollectionsMarshal.AsSpan(list: m_hashScratch);

            ordered.Sort(comparison: static (left, right) => {
                var result = left.Left.CompareTo(value: right.Left);

                if (result != 0) {
                    return result;
                }
                result = left.Binding.Right.CompareTo(value: right.Binding.Right);
                if (result != 0) {
                    return result;
                }
                result = left.Binding.LeftGeneration.CompareTo(value: right.Binding.LeftGeneration);
                return ((result != 0) ? result : left.Binding.RightGeneration.CompareTo(value: right.Binding.RightGeneration));
            });
            hash.Add(value: ((uint)ordered.Length));

            foreach (var entry in ordered) {
                hash.Add(value: entry.Left);
                hash.Add(value: entry.Binding.Right);
                hash.Add(value: entry.Binding.LeftGeneration);
                hash.Add(value: entry.Binding.RightGeneration);
                hash.Add(value: ((byte)(entry.Held
                    ? 1
                    : 0)));
            }
        }
    }
    // The layout-free identity of a binding's left side: the key name's own digest for a key-bound entry, the index
    // itself for a participant or an unbound (-1) entry.
    private static ulong LeftIdentity(LatchKey binding, IReadOnlyList<CellName>? keyNames) => (
        ((keyNames is not null) && (binding.Left >= 0) && (binding.Left < keyNames.Count))
            ? Fnv1aHash.Compute(values: keyNames[binding.Left].Value.AsSpan())
            : unchecked((ulong)((long)binding.Left))
    );

    private readonly record struct HashEntry(LatchKey Binding, bool Held, ulong Left);
    /// <summary>Opens a sweep over one rule's bindings; entries the sweep does not touch are closed by
    /// <see cref="EndSweep"/>.</summary>
    public void BeginSweep() => m_sweep++;
    /// <summary>Returns one rule's state, minting it on first use.</summary>
    /// <param name="name">The rule's name.</param>
    /// <returns>The state.</returns>
    public RuleState Rule(string name) {
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: m_byRule,
            exists: out _,
            key: name
        );

        return (state ??= new RuleState());
    }
    /// <summary>Forgets every bound entry and every cached verdict while keeping the storage each rule has grown,
    /// so a caller that judges many positions from a clean latch allocates nothing per position.</summary>
    /// <remarks>The latch then reads as empty: <see cref="Count"/> is zero and every crossing is a first one. A
    /// rule's emptied storage is still enumerated by name, so a latch that is hashed or checkpointed uses
    /// <see cref="Clear"/>.</remarks>
    public void Reset() {
        SchedulerEpoch++;
        foreach (var state in m_byRule.Values) {
            state.Held.Clear();
            Forget(schedule: state.Once);
            foreach (var schedule in state.Scheduling.Values) {
                Forget(schedule: schedule);
            }
        }

        static void Forget(BindingSchedule? schedule) {
            if (schedule is null) {
                return;
            }
            schedule.Touched = 0UL;
            schedule.GateOwner = null;
            if (schedule.Memos is { } memos) {
                Array.Clear(array: memos);
            }
        }
    }
    /// <summary>Forgets every entry, including the scheduler's own caches and the spent counts.</summary>
    public void Clear() {
        m_byRule.Clear();
        m_statesFor = null;
        SpentTicks = 0L;
        SchedulerEpoch++;
    }

    /// <summary>Gets a count that moves whenever the latch forgets scheduler observations (<see cref="Clear"/>,
    /// <see cref="Reset"/>, <see cref="InvalidateScheduler"/>), and with them any verdict a due list relied on: a due
    /// list that sees it move takes every rule as due.</summary>
    public long SchedulerEpoch { get; private set; }

    // The states of the compiled family States last resolved, by compiled position. A family's array is replaced on
    // every recompile, so its identity names the resolution; Clear drops the states it holds and the cache with them.
    private CompiledRule[]? m_statesFor;
    private RuleState[] m_states = [];

    /// <summary>Returns every rule's state for one compiled family, by compiled position, resolving each name once per
    /// compiled array rather than once per evaluation.</summary>
    /// <param name="rules">The compiled family.</param>
    /// <returns>The states, aligned with <paramref name="rules"/>.</returns>
    internal RuleState[] States(CompiledRule[] rules) {
        if (!ReferenceEquals(objA: m_statesFor, objB: rules)) {
            var states = new RuleState[rules.Length];

            for (var index = 0; (index < rules.Length); index++) {
                states[index] = Rule(name: rules[index].Name);
            }
            m_states = states;
            m_statesFor = rules;
        }

        return m_states;
    }
    /// <summary>Forgets scheduler observations while preserving every edge-held binding.</summary>
    /// <remarks>Use this when the backing arena is replaced by an equivalent layout whose row-version counters
    /// begin again. The held bits describe rule semantics and survive; cached versions and lexical values belong
    /// to the retired arena.</remarks>
    public void InvalidateScheduler() {
        SchedulerEpoch++;
        foreach (var state in m_byRule.Values) {
            Invalidate(schedule: state.Once);
            foreach (var schedule in state.Scheduling.Values) {
                Invalidate(schedule: schedule);
            }
        }

        static void Invalidate(BindingSchedule? schedule) {
            if (schedule is null) {
                return;
            }
            schedule.GateOwner = null;
            foreach (var memo in (schedule.Memos ?? [])) {
                if (memo is not null) {
                    memo.Owner = null;
                }
            }
        }
    }
    /// <summary>Closes the sweep: every binding of the rule not touched since <see cref="BeginSweep"/> is removed,
    /// with the scheduler's caches for it.</summary>
    /// <param name="state">The swept rule's state.</param>
    public void EndSweep(RuleState state) {
        ArgumentNullException.ThrowIfNull(argument: state);

        // Dictionary.Remove does not invalidate an in-flight enumerator.
        foreach (var pair in state.Held) {
            if (!Touched(state: state, binding: pair.Key)) {
                _ = state.Held.Remove(key: pair.Key);
            }
        }
        foreach (var pair in state.Scheduling) {
            if (pair.Value.Touched != m_sweep) {
                _ = state.Scheduling.Remove(key: pair.Key);
            }
        }
    }
    /// <summary>Appends every entry as its rule name, its binding, and whether the gate held — what
    /// <see cref="Restore"/> reads back.</summary>
    /// <param name="into">The list to append to.</param>
    /// <remarks>A binding's <see cref="LatchKey.Left"/> is an interned ordinal for a key-bound entry, so a caller
    /// persisting this must carry the key's NAME and re-intern it before restoring under another catalog.</remarks>
    public void Flatten(List<(string Rule, LatchKey Binding, bool Held)> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var (name, state) in m_byRule) {
            foreach (var (binding, held) in state.Held) {
                into.Add(item: (name, binding, held));
            }
        }
    }
    /// <summary>Returns whether the gate held at the last evaluation of any binding of the rule.</summary>
    /// <param name="name">The rule's name.</param>
    /// <returns><see langword="true"/> when some binding held.</returns>
    public bool Held(string name) {
        if (!m_byRule.TryGetValue(
            key: name,
            value: out var state
        )) {
            return false;
        }

        foreach (var held in state.Held.Values) {
            if (held) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Keeps every surviving name's entries and drops the names no longer compiled.</summary>
    /// <param name="compiled">The family's compiled rules after a recompile.</param>
    public void Prune(CompiledRule[] compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        if (m_byRule.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rule in compiled) {
            _ = live.Add(item: rule.Name);
        }

        foreach (var name in m_byRule.Keys) {
            if (!live.Contains(item: name) && m_byRule.Remove(key: name)) {
                m_statesFor = null;
            }
        }
    }
    /// <summary>Restores one flattened entry.</summary>
    /// <param name="name">The rule's name.</param>
    /// <param name="binding">The binding the entry belongs to.</param>
    /// <param name="held">Whether the gate held.</param>
    public void Restore(string name, LatchKey binding, bool held) {
        ArgumentNullException.ThrowIfNull(argument: name);

        Rule(name: name).Held[binding] = held;
        SchedulerEpoch++;
    }
    /// <summary>Marks a binding as evaluated in the open sweep.</summary>
    /// <param name="state">The rule's state.</param>
    /// <param name="binding">The binding.</param>
    public void Touch(RuleState state, LatchKey binding) {
        ArgumentNullException.ThrowIfNull(argument: state);

        state.Binding(binding: binding).Touched = m_sweep;
    }

    internal void Touch(BindingSchedule schedule) => schedule.Touched = m_sweep;
    // Whether a binding was touched since the open sweep began.
    internal bool TouchedInSweep(RuleState state, LatchKey binding) => Touched(state: state, binding: binding);
    // Forgets one binding of a swept rule — its edge bit and its scheduler caches — exactly as EndSweep forgets a
    // binding the sweep did not touch: what a partial sweep does for each binding it visits and leaves untouched.
    internal void Forget(RuleState state, LatchKey binding) {
        _ = state.Held.Remove(key: binding);
        _ = state.Scheduling.Remove(key: binding);
    }

    private bool Touched(RuleState state, LatchKey binding) => (binding.Equals(other: LatchKey.None)
        ? (state.Once?.Touched == m_sweep)
        : (state.Scheduling.TryGetValue(key: binding, value: out var schedule) && (schedule.Touched == m_sweep)));
}
