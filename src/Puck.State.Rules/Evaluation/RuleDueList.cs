namespace Puck.State.Rules;

/// <summary>One compiled family's due list: which rules (and, for a body sweep, which bodies) must be visited this
/// tick, and the dependency index that pushes a rule onto it when something it reads moves. Built per compiled
/// array, latch and host fact epoch; never hashed, and dropped whenever anything it relied on could have moved
/// unseen (the arena replaced, the latch's scheduler caches forgotten), which marks every rule due.</summary>
/// <remarks>
/// <para>The invariant is <see cref="RuleEvaluator"/>'s: a rule left off the list is one whose in-place evaluation
/// would reproduce its last verdict bit for bit. A rule goes on when its schedule is volatile or names a sweep set no
/// version covers (always), when its last visit left a verdict to judge again (a Level gate that held, a refusal),
/// or when a row or host fact its schedule names moved. The only producers of a move are the arena's moved-row feed,
/// fed by the one door every row version passes (<see cref="StateArena.DrainMovedRows"/>), and the host's moved-fact
/// feed (<see cref="IStateReader.DrainFactMoves"/>), whose versions are what <see cref="RuleEvaluator"/>'s version door
/// reads; a dependency neither feed can report keeps its schedule volatile.</para>
/// <para>Order: a mark lands where the walk has not yet reached (this tick) or has passed (next tick), which is exactly
/// where an in-place version check would first see the move.</para>
/// </remarks>
internal sealed class RuleDueList {
    private enum Kind : byte {
        Always,
        Once,
        RowSweep,
        BodySweep,
    }

    private readonly RuleEvaluator m_evaluator;
    private readonly IStateReader m_host;
    private readonly Kind[] m_kinds;
    private readonly ulong[] m_due;
    private readonly ulong[] m_visited;
    private readonly bool[] m_full;
    private readonly ulong[] m_wake;
    private readonly ulong[]?[] m_bodies;
    private readonly ulong[]?[] m_live;
    private readonly int m_epoch;
    private readonly Dictionary<int, int[]> m_rowReaders;
    private readonly Dictionary<(int Source, int Body), int[]> m_literalFactReaders;
    // Every literal reader of a source, whatever body it names: a move the host reports for the source as a whole
    // (FactRead.Whole) reaches each of them, as it reaches the source's $each and whole readers.
    private readonly Dictionary<int, int[]> m_literalFactReadersBySource;
    private readonly Dictionary<int, int[]> m_eachFactReaders;
    private readonly Dictionary<int, int[]> m_wholeFactReaders;
    private readonly List<int> m_rows = [];
    private readonly List<FactRead> m_facts = [];
    private readonly List<int> m_liveNow = [];

    private StateArena? m_arena;
    private long m_latchEpoch = -1L;

    /// <summary>Initializes a due list over one compiled family, every rule due.</summary>
    /// <param name="evaluator">The evaluator whose schedules and host the list reads.</param>
    /// <param name="rules">The compiled family.</param>
    /// <param name="latch">The family's edge latch.</param>
    public RuleDueList(RuleEvaluator evaluator, CompiledRule[] rules, RuleLatch latch) {
        m_evaluator = evaluator;
        m_host = evaluator.Host;
        m_epoch = m_host.FactScheduleEpoch;
        Rules = rules;
        Latch = latch;
        States = latch.States(rules: rules);
        m_kinds = new Kind[rules.Length];
        m_due = new ulong[((rules.Length + 63) >> 6)];
        m_visited = new ulong[m_due.Length];
        m_full = new bool[rules.Length];
        m_wake = new ulong[rules.Length];
        Array.Fill(array: m_wake, value: ulong.MaxValue);
        m_bodies = new ulong[]?[rules.Length];
        m_live = new ulong[]?[rules.Length];

        var rowReaders = new Dictionary<int, List<int>>();
        var literal = new Dictionary<(int Source, int Body), List<int>>();
        var literalBySource = new Dictionary<int, List<int>>();
        var each = new Dictionary<int, List<int>>();
        var whole = new Dictionary<int, List<int>>();

        static void Add<TKey>(Dictionary<TKey, List<int>> index, TKey key, int rule) where TKey : notnull {
            if (!index.TryGetValue(key: key, value: out var readers)) {
                index[key] = readers = [];
            }
            if ((readers.Count == 0) || (readers[^1] != rule)) {
                readers.Add(item: rule);
            }
        }

        for (var index = 0; (index < rules.Length); index++) {
            var rule = rules[index];
            var schedule = evaluator.ScheduleOf(rule: rule, state: States[index]);
            var kind = Classify(evaluator: evaluator, rule: rule, schedule: schedule);

            m_kinds[index] = kind;
            if (kind == Kind.Always) {
                continue;
            }
            foreach (var row in schedule.Rows) {
                Add(index: rowReaders, key: row, rule: index);
            }
            foreach (var row in schedule.IterationRows) {
                Add(index: rowReaders, key: row, rule: index);
            }
            foreach (var fact in schedule.Facts) {
                switch (fact.Body) {
                    case FactRead.EachBody:
                        Add(index: each, key: fact.Source, rule: index);
                        break;
                    case FactRead.Whole:
                        Add(index: whole, key: fact.Source, rule: index);
                        break;
                    default:
                        Add(index: literal, key: (fact.Source, fact.Body), rule: index);
                        Add(index: literalBySource, key: fact.Source, rule: index);
                        break;
                }
            }
            foreach (var fact in schedule.IterationFacts) {
                Add(index: whole, key: fact.Source, rule: index);
            }
        }

        m_rowReaders = Freeze(index: rowReaders);
        m_literalFactReaders = Freeze(index: literal);
        m_literalFactReadersBySource = Freeze(index: literalBySource);
        m_eachFactReaders = Freeze(index: each);
        m_wholeFactReaders = Freeze(index: whole);
        MarkAll();
    }

    /// <summary>Gets the compiled family.</summary>
    public CompiledRule[] Rules { get; }
    /// <summary>Gets the family's edge latch.</summary>
    public RuleLatch Latch { get; }
    /// <summary>Gets each rule's latch state, aligned with <see cref="Rules"/>.</summary>
    public RuleLatch.RuleState[] States { get; }

    /// <summary>Returns whether the list was built for this family, latch and host fact epoch.</summary>
    public bool Covers(CompiledRule[] rules, RuleLatch latch, int epoch) => (
        ReferenceEquals(objA: Rules, objB: rules) &&
        ReferenceEquals(objA: Latch, objB: latch) &&
        (m_epoch == epoch)
    );
    /// <summary>Returns the first due rule at or after <paramref name="from"/>, or -1.</summary>
    public int NextDue(int from) {
        for (var word = (from >> 6); (word < m_due.Length); word++) {
            var bits = m_due[word];

            if (word == (from >> 6)) {
                bits &= (ulong.MaxValue << (from & 63));
            }
            if (bits != 0UL) {
                return ((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(value: bits));
            }
        }

        return -1;
    }
    /// <summary>Takes a rule off the list for its visit and records the visit.</summary>
    public void Visit(int index) {
        m_due[(index >> 6)] &= ~(1UL << (index & 63));
        m_visited[(index >> 6)] |= (1UL << (index & 63));
    }
    /// <summary>Returns whether the rule is a body sweep the list tracks per body.</summary>
    public bool IsBodySweep(int index) => (m_kinds[index] == Kind.BodySweep);
    /// <summary>Puts a visited rule back on the list for next tick when its verdict must be judged again: always for a
    /// rule no schedule names, when any binding's verdict asks for it, and for a body sweep while any body (or the
    /// whole sweep) is still due.</summary>
    public void Settle(int index, bool rerun) {
        var again = m_kinds[index] switch {
            Kind.Always => true,
            Kind.BodySweep => (m_full[index] || AnyBody(index: index)),
            _ => rerun,
        };

        if (again) {
            SetDue(index: index);
        }
    }
    /// <summary>Records the earliest deadline a visit's verdicts must be judged again at. A visit that judged or kept
    /// every binding replaces the rule's wake; one that judged only some bodies keeps the earlier of the two, since
    /// the bindings it passed over keep theirs.</summary>
    public void RecordWake(int index, ulong wake, bool whole) {
        if (m_kinds[index] == Kind.Always) {
            return;
        }
        m_wake[index] = (whole ? wake : Math.Min(val1: m_wake[index], val2: wake));
    }
    /// <summary>Marks every rule whose earliest deadline has come due in full, so each of its bindings is judged
    /// against the tick that may now open it.</summary>
    public void Wake(ulong tick) {
        for (var index = 0; (index < m_wake.Length); index++) {
            if (m_wake[index] <= tick) {
                m_wake[index] = ulong.MaxValue;
                MarkFull(index: index);
            }
        }
    }
    /// <summary>Counts the rules the walk did not visit into each one's <see cref="RuleLatch.RuleSpent.Elided"/>, and
    /// forgets the walk's visits.</summary>
    /// <returns>How many rules the walk did not visit.</returns>
    public long CountElided() {
        var elided = 0L;

        for (var index = 0; (index < Rules.Length); index++) {
            if ((m_visited[(index >> 6)] & (1UL << (index & 63))) == 0UL) {
                States[index].Spent.Elided++;
                elided++;
            }
        }
        Array.Clear(array: m_visited);

        return elided;
    }
    /// <summary>Marks every rule due in full.</summary>
    public void MarkAll() {
        for (var index = 0; (index < Rules.Length); index++) {
            MarkFull(index: index);
        }
    }
    /// <summary>Marks every rule compiled under a name due in full.</summary>
    public void MarkNamed(string name) {
        for (var index = 0; (index < Rules.Length); index++) {
            if (string.Equals(a: Rules[index].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                MarkFull(index: index);
            }
        }
    }
    /// <summary>Pushes every move the arena and the host recorded since the last drain onto the list: the readers of
    /// each moved row, and the readers of each moved host fact (only the moved body of a body sweep reading it for
    /// <c>$each</c>). A replaced arena, a latch whose scheduler caches were forgotten, or a feed that reports every
    /// dependency moved marks every rule due in full.</summary>
    public void Drain() {
        var arena = m_host.Arena;

        if (!ReferenceEquals(objA: arena, objB: m_arena)) {
            // A replaced store restarts its versions, so a cache captured against the old one proves nothing.
            if (m_arena is not null) {
                Latch.InvalidateScheduler();
            }
            m_arena = arena;
            arena.OpenMoveFeed();
            MarkAll();
        }
        if (Latch.SchedulerEpoch != m_latchEpoch) {
            m_latchEpoch = Latch.SchedulerEpoch;
            MarkAll();
        }

        m_rows.Clear();
        if (arena.DrainMovedRows(into: m_rows)) {
            MarkAll();
        } else {
            foreach (var row in m_rows) {
                if (m_rowReaders.TryGetValue(key: row, value: out var readers)) {
                    foreach (var reader in readers) {
                        MarkFull(index: reader);
                    }
                }
            }
        }

        m_facts.Clear();
        if (m_host.DrainFactMoves(into: m_facts)) {
            MarkAll();

            return;
        }
        foreach (var moved in m_facts) {
            if ((moved.Body >= 0) && m_literalFactReaders.TryGetValue(key: (moved.Source, moved.Body), value: out var literal)) {
                foreach (var reader in literal) {
                    MarkFull(index: reader);
                }
            }
            if ((moved.Body == FactRead.Whole) && m_literalFactReadersBySource.TryGetValue(key: moved.Source, value: out var anyLiteral)) {
                foreach (var reader in anyLiteral) {
                    MarkFull(index: reader);
                }
            }
            if (m_eachFactReaders.TryGetValue(key: moved.Source, value: out var each)) {
                foreach (var reader in each) {
                    MarkBody(index: reader, body: moved.Body);
                }
            }
            if (m_wholeFactReaders.TryGetValue(key: moved.Source, value: out var whole)) {
                foreach (var reader in whole) {
                    MarkBody(index: reader, body: moved.Body);
                }
            }
        }
    }
    /// <summary>Returns whether a body sweep is due in full, and clears that.</summary>
    public bool TakeFull(int index) {
        var full = m_full[index];

        m_full[index] = false;
        if (full && (m_bodies[index] is { } bodies)) {
            Array.Clear(array: bodies);
        }

        return full;
    }
    /// <summary>Returns whether a body sweep became due in full while it runs.</summary>
    public bool IsFull(int index) => m_full[index];
    /// <summary>Returns whether one body of a body sweep is due.</summary>
    public bool IsBodyDue(int index, int body) => (
        (m_bodies[index] is { } bodies) &&
        ((body >> 6) < bodies.Length) &&
        ((bodies[(body >> 6)] & (1UL << (body & 63))) != 0UL)
    );
    /// <summary>Takes one body of a body sweep off the list for its evaluation.</summary>
    public void ClearBody(int index, int body) {
        if ((m_bodies[index] is { } bodies) && ((body >> 6) < bodies.Length)) {
            bodies[(body >> 6)] &= ~(1UL << (body & 63));
        }
    }
    /// <summary>Keeps one body of a body sweep due for next tick: its verdict must be judged again.</summary>
    public void MarkBodyNext(int index, int body) => MarkBody(body: body, index: index);
    /// <summary>Records the bodies live as a body sweep's visit begins: the set its bindings are judged over, as a full
    /// sweep snapshots its keys.</summary>
    public void SnapshotLive(int index, List<int> live) {
        var words = ((Max(live: live) + 64) >> 6);
        var set = m_live[index];

        if ((set is null) || (set.Length < words)) {
            m_live[index] = set = new ulong[Math.Max(val1: words, val2: (set?.Length ?? 0))];
        } else {
            Array.Clear(array: set);
        }
        foreach (var body in live) {
            set[(body >> 6)] |= (1UL << (body & 63));
        }

        static int Max(List<int> live) => ((live.Count == 0) ? 0 : live[^1]);
    }
    /// <summary>Closes a body sweep's visit over the bodies still due that the visit did not judge because they were
    /// not live as it began: each one's binding is forgotten as a full sweep's end forgets a key it no longer holds
    /// (already done by that end after a full sweep), and the body stays due only when it is live now, having joined
    /// during the sweep.</summary>
    public void ForgetUnswept(int index, ArenaJournal journal, CellKeyTable keys, RuleLatch latch, RuleLatch.RuleState state, bool swept) {
        if ((m_bodies[index] is not { } bodies) || !AnyBody(index: index)) {
            return;
        }

        var live = m_live[index];
        var gathered = false;

        for (var word = 0; (word < bodies.Length); word++) {
            var bits = bodies[word] & ~(((live is not null) && (word < live.Length)) ? live[word] : 0UL);

            while (bits != 0UL) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(value: bits);
                var body = ((word << 6) + bit);

                bits &= (bits - 1UL);
                if (!swept && RuleReads.TryIndexKey(index: body, key: out var key, keys: keys)) {
                    latch.Forget(binding: new LatchKey(Left: key.Ordinal, Right: -1), journal: journal, state: state);
                }
                if (!gathered) {
                    m_liveNow.Clear();
                    ((ILiveBodies)m_host).CollectLiveBodies(into: m_liveNow);
                    gathered = true;
                }
                if (m_liveNow.BinarySearch(item: body) < 0) {
                    bodies[word] &= ~(1UL << bit);
                }
            }
        }
    }

    private static Kind Classify(RuleEvaluator evaluator, CompiledRule rule, RuleSchedule schedule) {
        if (
            evaluator.Owns(rule: rule) ||
            schedule.Volatile ||
            !schedule.IterationVersioned ||
            (rule.MaxFirings > 0) ||
            (rule.Zones is { References.Count: > 0 })
        ) {
            return Kind.Always;
        }
        if (rule.ForEachBodies) {
            return Kind.BodySweep;
        }

        return ((rule.ForEachOrdinal >= 0) ? Kind.RowSweep : Kind.Once);
    }
    private static Dictionary<TKey, int[]> Freeze<TKey>(Dictionary<TKey, List<int>> index) where TKey : notnull {
        var frozen = new Dictionary<TKey, int[]>(capacity: index.Count);

        foreach (var (key, readers) in index) {
            frozen[key] = [.. readers];
        }

        return frozen;
    }
    private void SetDue(int index) => m_due[(index >> 6)] |= (1UL << (index & 63));
    private void MarkFull(int index) {
        SetDue(index: index);
        m_full[index] = true;
    }
    // A move of one body's fact: that body of a body sweep, or the whole rule otherwise. A move reported for the
    // source as a whole marks the rule in full.
    private void MarkBody(int index, int body) {
        if ((m_kinds[index] != Kind.BodySweep) || (body < 0)) {
            MarkFull(index: index);

            return;
        }

        var bodies = m_bodies[index];
        var words = ((body >> 6) + 1);

        if ((bodies is null) || (bodies.Length < words)) {
            var grown = new ulong[Math.Max(val1: words, val2: ((bodies?.Length ?? 0) * 2))];

            bodies?.CopyTo(array: grown, index: 0);
            m_bodies[index] = bodies = grown;
        }
        bodies[(body >> 6)] |= (1UL << (body & 63));
        SetDue(index: index);
    }
    private bool AnyBody(int index) {
        if (m_bodies[index] is not { } bodies) {
            return false;
        }
        foreach (var word in bodies) {
            if (word != 0UL) {
                return true;
            }
        }

        return false;
    }
}
