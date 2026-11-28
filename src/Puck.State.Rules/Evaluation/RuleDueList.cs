using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>One compiled family's due list: which rules, and which bindings of a keyed sweep, must be visited this
/// tick, and the dependency index that pushes a rule or one of its bindings onto it when something it reads moves.
/// Built per compiled array, latch and host fact epoch; never hashed, and dropped whenever anything it relied on could
/// have moved unseen (the arena replaced, the latch's scheduler caches forgotten), which marks every rule due.</summary>
/// <remarks>
/// <para>The invariant is <see cref="RuleEvaluator"/>'s: a binding left off the list is one whose in-place evaluation
/// would reproduce its last verdict bit for bit. A rule goes on in full when its schedule is volatile or names a
/// sweep set no version covers (always), when its last visit left a verdict to judge again (a Level gate that held,
/// a refusal), or when something every one of its bindings reads moved. One binding of a keyed sweep goes on when a
/// cell it reads under its own key, or a host fact read for its own body, moved (<see cref="RowReach.Each"/>,
/// <see cref="FactRead.EachBody"/>). The only producers of a move are the arena's moved-cell feed, fed by the one
/// door every committed change is described through (<see cref="StateArena.TryReadCellMoves"/>), and the host's
/// moved-fact feed (<see cref="IStateReader.DrainFactMoves"/>), whose versions are what <see cref="RuleEvaluator"/>'s
/// version door reads; a dependency neither feed can report keeps its schedule volatile.</para>
/// <para>Order: a mark lands where the walk has not yet reached (this tick) or has passed (next tick), which is exactly
/// where an in-place version check would first see the move.</para>
/// </remarks>
internal sealed class RuleDueList {
    /// <summary>How a rule is visited when it is due.</summary>
    public enum Visit : byte {
        /// <summary>Every tick, in full.</summary>
        Always,

        /// <summary>When due, in full.</summary>
        Whole,

        /// <summary>A keyed row's sweep, due per key.</summary>
        RowSweep,

        /// <summary>A sweep whose keys are body indices (the live bodies, or a host set that binds bodies), due per body.</summary>
        BodySweep,

        /// <summary>A sweep over a row family's members, due per member and key.</summary>
        FamilySweep,
    }

    // The keys a swept row held when a sweep last snapshotted it, shared by every rule sweeping the row. It stands
    // while the row's structure version does: the row then holds the same cells in the same order, so a position
    // names the same binding for every rule and across ticks.
    public sealed class RowSnapshot {
        public CellKey[] Keys = [];
        public int Count;
        public ulong Structure = ulong.MaxValue;

        private readonly Dictionary<int, int> m_positions = [];
        private bool m_indexed;

        public void Take(List<CellKey> keys, ulong structure) {
            if (Keys.Length < keys.Count) {
                Keys = new CellKey[Math.Max(val1: keys.Count, val2: (Keys.Length * 2))];
            }
            keys.CopyTo(array: Keys);
            Count = keys.Count;
            Structure = structure;
            m_indexed = false;
        }
        // A key's position in the snapshot, indexed on the first lookup after a snapshot. Keys are matched by
        // ordinal, which names one key of the row while its structure stands whichever table handed the key out.
        public bool TryPositionOf(CellKey key, out int position) {
            if (!m_indexed) {
                m_positions.Clear();
                for (var index = 0; (index < Count); index++) {
                    m_positions[Keys[index].Ordinal] = index;
                }
                m_indexed = true;
            }

            return m_positions.TryGetValue(key: key.Ordinal, value: out position);
        }
    }

    // The (member, key) bindings a family's members held when a sweep last snapshotted them, shared by every rule
    // sweeping the family: in family order, then each member's cell order. It stands while every member's structure
    // version does, so a position names the same binding for every rule and across ticks.
    public sealed class FamilySnapshot {
        public CellKey[] Keys = [];
        public int[] Members = [];
        public int Count;
        public long Generation;

        private ulong[] m_structures = [];
        private int[] m_starts = [];
        private readonly Dictionary<(int Member, int Key), int> m_positions = [];
        private readonly Dictionary<int, List<int>> m_byKey = [];
        private bool m_indexed;

        public void Take(List<CellKey> keys, List<int> members, StateArena arena, RowFamily family, int span) {
            if (Keys.Length < keys.Count) {
                Keys = new CellKey[Math.Max(val1: keys.Count, val2: (Keys.Length * 2))];
                Members = new int[Keys.Length];
            }
            keys.CopyTo(array: Keys);
            members.CopyTo(array: Members);
            Count = keys.Count;
            if (m_starts.Length != (span + 2)) {
                m_starts = new int[span + 2];
            }
            // m_starts[m] is member m's first position and m_starts[m + 1] one past its last; a gap holds none.
            var position = 0;

            for (var member = 0; (member <= (span + 1)); member++) {
                while ((position < Count) && (Members[position] < member)) {
                    position++;
                }
                m_starts[member] = position;
            }
            m_structures = Structures(arena: arena, family: family, into: m_structures);
            Generation++;
            m_indexed = false;
        }
        // Whether every member still holds the cells, in the order, the snapshot was taken over.
        public bool Stands(StateArena arena, RowFamily family) {
            if (Generation == 0L) {
                return false;
            }

            var index = 0;

            foreach (var ordinal in family.Ordinals()) {
                if ((index >= m_structures.Length) || (m_structures[index++] != arena.RowStructureVersion(rowOrdinal: ordinal))) {
                    return false;
                }
            }

            return (index == m_structures.Length);
        }
        // The positions of one member's bindings, empty for a member the snapshot holds none of.
        public (int Start, int End) RangeOf(int member) => ((((uint)member) < ((uint)(m_starts.Length - 1)))
            ? (m_starts[member], m_starts[member + 1])
            : (0, 0));
        public bool TryPositionOf(int member, CellKey key, out int position) {
            Index();

            return m_positions.TryGetValue(key: (member, key.Ordinal), value: out position);
        }
        // Every position whose key is this key, one per member that holds it.
        public List<int>? PositionsOf(CellKey key) {
            Index();

            return m_byKey.GetValueOrDefault(key: key.Ordinal);
        }

        private void Index() {
            if (m_indexed) {
                return;
            }
            m_positions.Clear();
            m_byKey.Clear();
            for (var index = 0; (index < Count); index++) {
                m_positions[(Members[index], Keys[index].Ordinal)] = index;
                if (!m_byKey.TryGetValue(key: Keys[index].Ordinal, value: out var positions)) {
                    m_byKey[Keys[index].Ordinal] = positions = [];
                }
                positions.Add(item: index);
            }
            m_indexed = true;
        }
        private static ulong[] Structures(StateArena arena, RowFamily family, ulong[] into) {
            var count = 0;

            foreach (var _ in family.Ordinals()) {
                count++;
            }
            if (into.Length != count) {
                into = new ulong[count];
            }

            var index = 0;

            foreach (var ordinal in family.Ordinals()) {
                into[index++] = arena.RowStructureVersion(rowOrdinal: ordinal);
            }

            return into;
        }
    }

    private readonly RuleEvaluator m_evaluator;
    private readonly IStateReader m_host;
    private readonly Visit[] m_kinds;
    private readonly ulong[] m_due;
    // The walk each rule was last visited in, or -1: what a rule's elided ticks are counted from when it is next
    // visited or read, so a walk that visits few rules costs nothing for the rest.
    private readonly long[] m_settledWalk;
    private long m_walk;
    private int m_visits;
    private readonly bool[] m_full;
    // The earliest deadline a rule's verdicts must be judged again at: for a keyed sweep, never later than the
    // earliest of its bindings' own (m_bindingWakes), which a lower one only costs a look at.
    private readonly ulong[] m_wake;
    // A keyed sweep's bindings' deadlines, by the binding the sweep's due bits name: what a deadline coming due marks,
    // so it judges the bindings whose deadline it is rather than the whole sweep. Every binding a visit judges records
    // its own (RuleEvaluator.SweepBinding), and a full visit judges every binding, so it starts the table afresh.
    private readonly Dictionary<int, ulong>?[] m_bindingWakes;
    // The due bindings of a keyed sweep: by body index for a body sweep, by position in the row's snapshot for a row
    // sweep.
    private readonly ulong[]?[] m_bindings;
    private readonly ulong[]?[] m_live;
    // A row sweep's snapshot, and the structure version of the row the rule's due positions were marked against.
    private readonly RowSnapshot?[] m_snapshots;
    private readonly ulong[] m_sweptStructure;
    // A family sweep's snapshot, and the snapshot generation the rule's due positions were marked against.
    private readonly FamilySnapshot?[] m_familySnapshots;
    private readonly long[] m_sweptGeneration;
    private readonly int m_epoch;
    // Readers by how they reach a row: every cell (any move of the row), the cell under $each (the binding the moved
    // key names), a literal cell (that cell's moves), and every reader of the row for a move that names it whole.
    private readonly Dictionary<int, int[]> m_anyRowReaders;
    private readonly Dictionary<int, int[]> m_eachRowReaders;
    private readonly Dictionary<(int Row, int Key), int[]> m_cellReaders;
    private readonly Dictionary<int, int[]> m_wholeRowReaders;
    // The keyed sweeps that read a row as the family member one binding's key selects, with that member's index.
    private readonly Dictionary<int, (int Rule, int Member)[]> m_memberRowReaders;
    // The family sweeps that read the cell under $each of the member $member selects: a move of cell k of member m's row
    // reaches member m's binding keyed k, and a move of the row whole member m's bindings.
    private readonly Dictionary<int, (int Rule, int Member)[]> m_memberEachRowReaders;
    // The family sweeps that read a row's cell under $member: a move of the cell whose key spells index m reaches
    // member m's bindings.
    private readonly Dictionary<int, int[]> m_memberKeyRowReaders;
    private readonly Dictionary<(int Source, int Body), int[]> m_literalFactReaders;
    // Every literal reader of a source, whatever body it names: a move the host reports for the source as a whole
    // (FactRead.Whole) reaches each of them, as it reaches the source's $each and whole readers.
    private readonly Dictionary<int, int[]> m_literalFactReadersBySource;
    private readonly Dictionary<int, int[]> m_eachFactReaders;
    private readonly Dictionary<int, int[]> m_wholeFactReaders;
    private readonly List<ArenaCellMove> m_moves = [];
    private readonly List<int> m_liveNow = [];

    private StateArena? m_arena;
    // The list's place in the arena's cell moves: every cell that moved before it has been pushed onto the list.
    private long m_movesRead = -1L;
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
        m_kinds = new Visit[rules.Length];
        m_due = new ulong[rules.Length.CeilingDivide(divisor: 64)];
        m_settledWalk = new long[rules.Length];
        m_full = new bool[rules.Length];
        m_wake = new ulong[rules.Length];
        Array.Fill(array: m_wake, value: ulong.MaxValue);
        m_bindingWakes = new Dictionary<int, ulong>?[rules.Length];
        m_bindings = new ulong[]?[rules.Length];
        m_live = new ulong[]?[rules.Length];
        m_snapshots = new RowSnapshot?[rules.Length];
        m_sweptStructure = new ulong[rules.Length];
        Array.Fill(array: m_sweptStructure, value: ulong.MaxValue);
        m_familySnapshots = new FamilySnapshot?[rules.Length];
        m_sweptGeneration = new long[rules.Length];

        var anyRow = new Dictionary<int, List<int>>();
        var eachRow = new Dictionary<int, List<int>>();
        var cell = new Dictionary<(int Row, int Key), List<int>>();
        var wholeRow = new Dictionary<int, List<int>>();
        var literal = new Dictionary<(int Source, int Body), List<int>>();
        var literalBySource = new Dictionary<int, List<int>>();
        var each = new Dictionary<int, List<int>>();
        var whole = new Dictionary<int, List<int>>();
        var snapshots = new Dictionary<int, RowSnapshot>();
        var familySnapshots = new Dictionary<string, FamilySnapshot>(comparer: StringComparer.Ordinal);
        var memberRow = new Dictionary<int, List<(int Rule, int Member)>>();
        var memberEachRow = new Dictionary<int, List<(int Rule, int Member)>>();
        var memberKeyRow = new Dictionary<int, List<int>>();

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
            if (kind == Visit.Always) {
                continue;
            }
            if (kind is (Visit.RowSweep or Visit.BodySweep or Visit.FamilySweep)) {
                m_bindingWakes[index] = [];
            }
            if (kind == Visit.RowSweep) {
                if (!snapshots.TryGetValue(key: rule.ForEachOrdinal, value: out var snapshot)) {
                    snapshots[rule.ForEachOrdinal] = snapshot = new RowSnapshot();
                }
                m_snapshots[index] = snapshot;
            }
            if ((kind == Visit.FamilySweep) && (rule.ForEachFamily is { } swept)) {
                if (!familySnapshots.TryGetValue(key: swept.Name.Value, value: out var familySnapshot)) {
                    familySnapshots[swept.Name.Value] = familySnapshot = new FamilySnapshot();
                }
                m_familySnapshots[index] = familySnapshot;
            }

            void Rows(RuleSchedule read) {
                for (var position = 0; (position < read.Rows.Length); position++) {
                    var row = read.Rows[position];
                    var reach = ((position < read.Reaches.Length) ? read.Reaches[position] : RowReach.Any);
                    var member = ((position < read.MemberIndices.Length) ? read.MemberIndices[position] : -1);

                    // A family member the binding's own key selects: any move of the row, a cell or the row whole,
                    // reaches the one binding whose key spells the member's index. Only a keyed sweep has such a
                    // binding; any other rule reading it, or a row read some other way too, is reached whole.
                    // A family sweep's swept rows are read for their members too, which the snapshot stands on: a move
                    // that changes a member's structure makes the sweep run in full when it is next visited.
                    var memberOnly = ((kind == Visit.FamilySweep) ? (reach & ~RowReach.Members) : reach);

                    if ((memberOnly == RowReach.Member) && (member >= 0) && (kind is Visit.RowSweep or Visit.BodySweep or Visit.FamilySweep)) {
                        if (!memberRow.TryGetValue(key: row, value: out var readers)) {
                            memberRow[row] = readers = [];
                        }
                        readers.Add(item: (index, member));

                        continue;
                    }
                    // The cell under $each of the member $member selects: a move of cell k reaches member m's binding
                    // keyed k, and a move of the row whole member m's bindings.
                    if ((memberOnly == RowReach.MemberEach) && (member >= 0) && (kind == Visit.FamilySweep)) {
                        if (!memberEachRow.TryGetValue(key: row, value: out var cellReaders)) {
                            memberEachRow[row] = cellReaders = [];
                        }
                        cellReaders.Add(item: (index, member));

                        continue;
                    }
                    // A cell under $member: a move of the cell keyed m reaches member m's bindings, and a move of the
                    // row whole every binding.
                    if ((kind == Visit.FamilySweep) && (memberOnly == RowReach.MemberKey)) {
                        Add(index: wholeRow, key: row, rule: index);
                        if (!memberKeyRow.TryGetValue(key: row, value: out var keyReaders)) {
                            memberKeyRow[row] = keyReaders = [];
                        }
                        keyReaders.Add(item: index);

                        continue;
                    }
                    if ((reach & (RowReach.Member | RowReach.MemberKey | RowReach.MemberEach)) != RowReach.None) {
                        reach = RowReach.Any;
                    }

                    Add(index: wholeRow, key: row, rule: index);
                    // Only a keyed sweep has a binding a moved key can name; any other rule that reads under $each is
                    // evaluated whole.
                    // A row named for its key set alone is reached only by a move that names it whole.
                    if (
                        ((reach & RowReach.Any) != RowReach.None) ||
                        (reach == RowReach.None) ||
                        (((reach & RowReach.Each) != RowReach.None) && (kind == Visit.Whole))
                    ) {
                        Add(index: anyRow, key: row, rule: index);
                    } else if ((reach & RowReach.Each) != RowReach.None) {
                        Add(index: eachRow, key: row, rule: index);
                    }
                }
                foreach (var read1 in read.Cells) {
                    Add(index: cell, key: (read1.RowOrdinal, read1.Key.Ordinal), rule: index);
                }
            }

            Rows(read: schedule);
            // A sweep's set changes only with the swept row's structure, which a move names whole.
            foreach (var row in schedule.IterationRows) {
                Add(index: wholeRow, key: row, rule: index);
            }
            // A Level firing taken as settled stands on everything the firing reads or writes, so a move of any of it
            // puts the rule, or the binding it reaches, back on the list.
            var firing = ((States[index].FiringSchedule is { Volatile: false } settles) ? settles : null);

            if (firing is not null) {
                Rows(read: firing);
            }
            foreach (var fact in schedule.Facts.Concat(schedule.IterationFacts).Concat(firing?.Facts ?? [])) {
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
        }

        m_anyRowReaders = Freeze(index: anyRow);
        m_eachRowReaders = Freeze(index: eachRow);
        m_cellReaders = Freeze(index: cell);
        m_wholeRowReaders = Freeze(index: wholeRow);
        m_literalFactReaders = Freeze(index: literal);
        m_literalFactReadersBySource = Freeze(index: literalBySource);
        m_eachFactReaders = Freeze(index: each);
        m_wholeFactReaders = Freeze(index: whole);
        m_memberRowReaders = memberRow.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => pair.Value.Distinct().ToArray());
        m_memberEachRowReaders = memberEachRow.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => pair.Value.Distinct().ToArray());
        m_memberKeyRowReaders = memberKeyRow.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => pair.Value.Distinct().ToArray());
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
    public int NextDue(int from) => NextBit(bits: m_due, from: from);
    /// <summary>Takes a rule off the list for its visit and records the visit, counting the walks since its last one
    /// into its <see cref="RuleLatch.RuleSpent.Elided"/>.</summary>
    public void BeginVisit(int index) {
        BitWords.Remove(index: index, words: m_due);
        States[index].Spent.Elided += (m_walk - m_settledWalk[index]);
        m_settledWalk[index] = (m_walk + 1L);
        m_visits++;
    }
    /// <summary>Returns how a rule is visited when it is due.</summary>
    public Visit KindOf(int index) => m_kinds[index];
    /// <summary>Puts a visited rule back on the list for next tick when its verdict must be judged again: always for a
    /// rule no schedule names, when any binding's verdict asks for it, and for a keyed sweep while any binding (or
    /// the whole sweep) is still due.</summary>
    public void Settle(int index, bool rerun) {
        var again = m_kinds[index] switch {
            Visit.Always => true,
            Visit.BodySweep or Visit.RowSweep or Visit.FamilySweep => (m_full[index] || AnyBinding(index: index)),
            _ => rerun,
        };

        if (again) {
            SetDue(index: index);
        }
    }
    /// <summary>Records the earliest deadline a visit's verdicts must be judged again at. A visit that judged or kept
    /// every binding replaces the rule's wake; one that judged only some bindings keeps the earlier of the two, since
    /// the bindings it passed over keep theirs.</summary>
    public void RecordWake(int index, ulong wake, bool whole) {
        if (m_kinds[index] == Visit.Always) {
            return;
        }
        m_wake[index] = (whole ? wake : Math.Min(val1: m_wake[index], val2: wake));
    }
    /// <summary>Records the deadline one binding of a keyed sweep must be judged again at, as its judgement just
    /// answered: <see cref="ulong.MaxValue"/> for none.</summary>
    /// <param name="index">The rule.</param>
    /// <param name="binding">The binding, as the sweep's due bits name it: a body's index, or a snapshot position.</param>
    /// <param name="wake">The binding's deadline.</param>
    public void RecordBindingWake(int index, int binding, ulong wake) {
        if (m_bindingWakes[index] is not { } wakes) {
            return;
        }
        if (wake == ulong.MaxValue) {
            wakes.Remove(key: binding);
        } else {
            wakes[binding] = wake;
        }
    }
    /// <summary>Marks what each deadline that has come due opens, so it is judged against the tick that may now open
    /// it: the bindings of a keyed sweep whose own deadline it is, while the positions they were recorded at still name
    /// them, and otherwise the whole rule.</summary>
    public void Wake(ulong tick) {
        for (var index = 0; (index < m_wake.Length); index++) {
            if (m_wake[index] > tick) {
                continue;
            }
            m_wake[index] = ulong.MaxValue;
            if ((m_bindingWakes[index] is not { } wakes) || !BindingsStand(index: index)) {
                m_bindingWakes[index]?.Clear();
                MarkFull(index: index);

                continue;
            }

            var earliest = ulong.MaxValue;

            foreach (var (binding, wake) in wakes) {
                if (wake <= tick) {
                    SetBinding(binding: binding, index: index);
                    wakes.Remove(key: binding);
                } else {
                    earliest = Math.Min(val1: earliest, val2: wake);
                }
            }
            m_wake[index] = earliest;
        }
    }
    // Whether a keyed sweep's binding numbers still name the bindings they were recorded for: a body's index always
    // does, and a position does while the snapshot it was taken in stands.
    private bool BindingsStand(int index) => (m_kinds[index] switch {
        Visit.BodySweep => true,
        Visit.RowSweep => SnapshotStands(index: index, structure: m_host.Arena.RowStructureVersion(rowOrdinal: Rules[index].ForEachOrdinal)),
        Visit.FamilySweep => FamilySnapshotStands(index: index),
        _ => false,
    });
    /// <summary>Closes one walk of the list and answers how many rules it did not visit. A rule's own
    /// <see cref="RuleLatch.RuleSpent.Elided"/> takes the walks it was left out of when it is next visited or when
    /// <see cref="SettleElided"/> runs, so closing a walk costs nothing per rule.</summary>
    /// <returns>How many rules the walk did not visit.</returns>
    public long CountElided() {
        var elided = (Rules.Length - m_visits);

        m_visits = 0;
        m_walk++;

        return elided;
    }
    /// <summary>Brings every rule's <see cref="RuleLatch.RuleSpent.Elided"/> up to the walks closed so far: what a
    /// read of the spent counts calls first.</summary>
    public void SettleElided() {
        for (var index = 0; (index < Rules.Length); index++) {
            var owed = (m_walk - m_settledWalk[index]);

            if (owed > 0L) {
                States[index].Spent.Elided += owed;
                m_settledWalk[index] = m_walk;
            }
        }
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
    /// each moved cell (only the binding a moved key names, for a keyed sweep that reads the cell under
    /// <c>$each</c>), and the readers of each moved host fact (only the moved body's binding, for a keyed sweep
    /// reading it for <c>$each</c>). A replaced arena, a latch whose scheduler caches were forgotten, or a feed that
    /// reports every dependency moved marks every rule due in full.</summary>
    public void Drain() {
        var arena = m_host.Arena;

        if (!ReferenceEquals(objA: arena, objB: m_arena)) {
            // A replaced store restarts its versions, so a cache captured against the old one proves nothing.
            if (m_arena is not null) {
                Latch.InvalidateScheduler();
            }
            m_arena = arena;
            m_movesRead = -1L;
            // Every row a schedule names is one the list follows; the read below takes every row as moved.
            foreach (var row in m_wholeRowReaders.Keys.Concat(m_memberRowReaders.Keys).Concat(m_memberEachRowReaders.Keys).Concat(m_memberKeyRowReaders.Keys)) {
                arena.WatchCellMoves(rowOrdinal: row);
            }
            // Another arena counts its structure versions from its own start, so no snapshot stands against it.
            Array.Fill(array: m_sweptStructure, value: ulong.MaxValue);
            foreach (var snapshot in m_snapshots) {
                snapshot?.Structure = ulong.MaxValue;
            }
            Array.Clear(array: m_sweptGeneration);
            MarkAll();
        }
        if (Latch.SchedulerEpoch != m_latchEpoch) {
            m_latchEpoch = Latch.SchedulerEpoch;
            MarkAll();
        }

        m_moves.Clear();
        if (!arena.TryReadCellMoves(into: m_moves, place: ref m_movesRead)) {
            MarkAll();
        } else {
            foreach (var move in m_moves) {
                if (m_memberRowReaders.TryGetValue(key: move.Row, value: out var members)) {
                    foreach (var (reader, member) in members) {
                        MarkMember(index: reader, member: member);
                    }
                }
                if (m_memberEachRowReaders.TryGetValue(key: move.Row, value: out var cells)) {
                    foreach (var (reader, member) in cells) {
                        if (move.Key.IsValid) {
                            MarkMemberKey(index: reader, key: move.Key, member: member);
                        } else {
                            MarkMember(index: reader, member: member);
                        }
                    }
                }
                if (!move.Key.IsValid) {
                    MarkReaders(index: m_wholeRowReaders, key: move.Row);

                    continue;
                }
                if (m_memberKeyRowReaders.TryGetValue(key: move.Row, value: out var keyReaders)) {
                    var member = (m_host.Arena.Keys.TryKeyInteger(integer: out var spelled, key: move.Key) ? spelled : -1L);

                    foreach (var reader in keyReaders) {
                        if ((member >= 0L) && (member <= int.MaxValue)) {
                            MarkMember(index: reader, member: (int)member);
                        }
                    }
                }
                MarkReaders(index: m_anyRowReaders, key: move.Row);
                MarkReaders(index: m_cellReaders, key: (move.Row, move.Key.Ordinal));
                if (m_eachRowReaders.TryGetValue(key: move.Row, value: out var readers)) {
                    foreach (var reader in readers) {
                        MarkKey(index: reader, key: move.Key);
                    }
                }
            }
        }

        m_evaluator.DrainFactMoves();
    }

    internal void ApplyFactMoves(bool all, List<FactRead> moves) {
        if (all) {
            MarkAll();

            return;
        }
        foreach (var moved in moves) {
            if (moved.Body >= 0) {
                MarkReaders(index: m_literalFactReaders, key: (moved.Source, moved.Body));
            }
            if (moved.Body == FactRead.Whole) {
                MarkReaders(index: m_literalFactReadersBySource, key: moved.Source);
            }
            if (m_eachFactReaders.TryGetValue(key: moved.Source, value: out var each)) {
                // A source that names its moves by key reaches the keyed sweep's binding under that key; any other
                // rule reading it for $each is judged whole.
                if ((moved.Body >= 0) && m_host.TryFactKey(body: moved.Body, key: out var movedKey, source: moved.Source)) {
                    foreach (var reader in each) {
                        if (m_kinds[reader] is Visit.RowSweep or Visit.FamilySweep) {
                            if (movedKey.IsValid) {
                                MarkKey(index: reader, key: movedKey);
                            }
                        } else {
                            MarkFull(index: reader);
                        }
                    }
                } else {
                    foreach (var reader in each) {
                        if (Binds(index: reader, body: moved.Body)) {
                            MarkBody(index: reader, body: moved.Body);
                        }
                    }
                }
            }
            if (m_wholeFactReaders.TryGetValue(key: moved.Source, value: out var whole)) {
                foreach (var reader in whole) {
                    MarkBody(index: reader, body: moved.Body);
                }
            }
        }
    }
    /// <summary>Returns whether a keyed sweep is due in full, and clears that with every binding's own mark.</summary>
    /// <param name="index">The rule.</param>
    /// <param name="force">Whether the sweep runs in full whatever was marked: its binding marks name nothing, since
    /// the positions they were made against no longer stand.</param>
    public bool TakeFull(int index, bool force = false) {
        var full = (m_full[index] | force);

        m_full[index] = false;
        if (full && (m_bindings[index] is { } bindings)) {
            Array.Clear(array: bindings);
        }
        if (full) {
            // A full visit judges every binding, and each records its deadline afresh.
            m_bindingWakes[index]?.Clear();
        }

        return full;
    }
    /// <summary>Returns whether a keyed sweep became due in full while it runs.</summary>
    public bool IsFull(int index) => m_full[index];
    /// <summary>Returns whether one body of a body sweep is due.</summary>
    public bool IsBodyDue(int index, int body) => (
        (m_bindings[index] is { } bodies) &&
        BitWords.Contains(index: body, words: bodies)
    );
    /// <summary>Takes one binding of a keyed sweep off the list for its evaluation: a body's index, or a row sweep's
    /// snapshot position.</summary>
    public void ClearBinding(int index, int binding) {
        if ((m_bindings[index] is { } bindings) && ((binding >> 6) < bindings.Length)) {
            BitWords.Remove(index: binding, words: bindings);
        }
    }
    /// <summary>Keeps one binding of a keyed sweep due for next tick: its verdict must be judged again.</summary>
    public void MarkBindingNext(int index, int binding) => SetBinding(binding: binding, index: index);
    /// <summary>Returns the first due snapshot position of a row sweep at or after <paramref name="from"/>, or -1.</summary>
    public int NextBindingDue(int index, int from) => ((m_bindings[index] is { } bindings) ? NextBit(bits: bindings, from: from) : -1);
    /// <summary>Returns a row sweep's snapshot of the swept row's keys.</summary>
    public RowSnapshot SnapshotOf(int index) => m_snapshots[index]!;
    /// <summary>Returns a family sweep's snapshot of the swept members' bindings.</summary>
    public FamilySnapshot FamilySnapshotOf(int index) => m_familySnapshots[index]!;
    /// <summary>Returns whether a family sweep's due positions still name the bindings they were marked for: the rule
    /// last swept in full over the snapshot as it stands, and every member still has the structure it was taken at.</summary>
    public bool FamilySnapshotStands(int index) => (
        (m_familySnapshots[index] is { } snapshot) &&
        (m_sweptGeneration[index] == snapshot.Generation) &&
        (Rules[index].ForEachFamily is { } family) &&
        snapshot.Stands(arena: m_host.Arena, family: family)
    );
    /// <summary>Records the snapshot generation a family sweep's full visit ran over.</summary>
    public void NoteFamilySwept(int index) => m_sweptGeneration[index] = m_familySnapshots[index]!.Generation;
    /// <summary>Returns whether a row sweep's due positions still name the bindings they were marked for: the rule
    /// last swept the row in full at the structure the snapshot was taken at, and the row still has it.</summary>
    public bool SnapshotStands(int index, ulong structure) => ((m_sweptStructure[index] == structure) && (m_snapshots[index]!.Structure == structure));
    /// <summary>Records the structure of the swept row a row sweep's full visit ran over.</summary>
    public void NoteSwept(int index, ulong structure) => m_sweptStructure[index] = structure;
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
            BitWords.Add(index: body, words: set);
        }

        static int Max(List<int> live) => ((live.Count == 0) ? 0 : live[^1]);
    }
    /// <summary>Closes a body sweep's visit over the bodies still due that the visit did not judge because they were
    /// not live as it began: each one's binding is forgotten as a full sweep's end forgets a key it no longer holds
    /// (already done by that end after a full sweep), and the body stays due only when it is live now, having joined
    /// during the sweep.</summary>
    public void ForgetUnswept(int index, ArenaJournal journal, CellKeyTable keys, RuleLatch latch, RuleLatch.RuleState state, bool swept) {
        if ((m_bindings[index] is not { } bodies) || !AnyBinding(index: index)) {
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
                if (!swept && keys.TryResolveIndex(index: body, key: out var key)) {
                    latch.Forget(binding: m_evaluator.SweepLatchKey(key: key, rule: Rules[index]), journal: journal, state: state);
                }
                if (!gathered) {
                    m_evaluator.CollectSweptBodies(into: m_liveNow, rule: Rules[index]);
                    gathered = true;
                }
                if (m_liveNow.BinarySearch(item: body) < 0) {
                    bodies[word] &= ~(1UL << bit);
                }
            }
        }
    }

    private static Visit Classify(RuleEvaluator evaluator, CompiledRule rule, RuleSchedule schedule) {
        // A sweep bounded by maxFirings is visited by change too: the bindings its cap leaves stay due
        // (RuleEvaluator's Defer), so it judges the bindings left and those something moved.
        if (
            schedule.Volatile ||
            !schedule.IterationVersioned ||
            (rule.Zones is { References.Count: > 0, PerBinding: false })
        ) {
            return Visit.Always;
        }
        // A rule its host evaluates binds what the host chooses, so no key or body of it is the list's to name.
        if (evaluator.Owns(rule: rule)) {
            return Visit.Whole;
        }

        return SweepKind(rule: rule);
    }
    /// <summary>Returns how a rule the list visits by change, and its host does not evaluate itself, is visited: per
    /// body, per family member and key, per key of one row, or whole. The work sheet reads the same answer for what a
    /// change or a deadline reaches (<see cref="RuleResponse.Of"/>).</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns>The visit.</returns>
    internal static Visit SweepKind(CompiledRule rule) {
        if (rule.ForEachBodies || (rule.Iteration is { BindsBodies: true })) {
            return Visit.BodySweep;
        }
        if (rule.ForEachFamily is not null) {
            return Visit.FamilySweep;
        }

        return (((rule.ForEachOrdinal >= 0) && (rule.Iteration is null)) ? Visit.RowSweep : Visit.Whole);
    }
    private static Dictionary<TKey, int[]> Freeze<TKey>(Dictionary<TKey, List<int>> index) where TKey : notnull {
        var frozen = new Dictionary<TKey, int[]>(capacity: index.Count);

        foreach (var (key, readers) in index) {
            frozen[key] = [.. readers];
        }

        return frozen;
    }
    private static int NextBit(ulong[] bits, int from) {
        for (var word = (from >> 6); (word < bits.Length); word++) {
            var held = bits[word];

            if (word == (from >> 6)) {
                held &= (ulong.MaxValue << (from & 63));
            }
            if (held != 0UL) {
                return ((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(value: held));
            }
        }

        return -1;
    }
    private void MarkReaders<TKey>(Dictionary<TKey, int[]> index, TKey key) where TKey : notnull {
        if (index.TryGetValue(key: key, value: out var readers)) {
            foreach (var reader in readers) {
                MarkFull(index: reader);
            }
        }
    }
    private void SetDue(int index) => BitWords.Add(index: index, words: m_due);
    private void MarkFull(int index) {
        SetDue(index: index);
        m_full[index] = true;
    }
    // A move of the cell one key names in a row the rule reads under $each: the binding that key is. A row sweep's
    // binding is the key's position in the swept row's snapshot, a key the row does not hold being no binding of it;
    // a family sweep's is that key's position in every member that holds it; a body sweep's is the body index the key
    // spells. A snapshot that no longer stands names no binding, so the rule
    // is due in full, as it is when it is no keyed sweep.
    private void MarkKey(int index, CellKey key) {
        switch (m_kinds[index]) {
            case Visit.FamilySweep when m_full[index]:
                return;
            case Visit.FamilySweep when FamilySnapshotStands(index: index):
                if (m_familySnapshots[index]!.PositionsOf(key: key) is { } positions) {
                    foreach (var held in positions) {
                        SetBinding(binding: held, index: index);
                    }
                }

                return;
            case Visit.RowSweep when m_full[index]:
                return;
            case Visit.RowSweep when SnapshotStands(index: index, structure: m_host.Arena.RowStructureVersion(rowOrdinal: Rules[index].ForEachOrdinal)):
                if (m_snapshots[index]!.TryPositionOf(key: key, position: out var position)) {
                    SetBinding(binding: position, index: index);
                }

                return;
            case Visit.BodySweep:
                if (m_host.Arena.Keys.TryCandidateIndex(index: out var body, key: key) && Binds(index: index, body: body)) {
                    SetBinding(binding: body, index: index);
                }

                return;
            default:
                MarkFull(index: index);

                return;
        }
    }
    // Whether a move read for one body, under $each, can reach a binding of a body sweep: the body was in the set the
    // sweep's last visit bound (SnapshotLive), so its binding stands. A body outside that set has no binding to judge
    // until the set itself moves, and every move of the set reaches the sweep through the set's own schedule (the live
    // bodies' move for the body that joined or left, a region's occupancy move in full), never through this read. A
    // sweep never visited yet is due in full, so nothing is filtered for it.
    private bool Binds(int index, int body) => (
        (m_kinds[index] != Visit.BodySweep) ||
        (body < 0) ||
        (m_live[index] is not { } live) ||
        BitWords.Contains(index: body, words: live)
    );
    // A move of one body's fact: that body's binding of a keyed sweep, or the whole rule otherwise. A move reported
    // for the source as a whole marks the rule in full. A row sweep's binding for a body is the key that spells the
    // body's index, which is what the host binds $each to for the read.
    private void MarkBody(int index, int body) {
        if (body < 0) {
            MarkFull(index: index);

            return;
        }
        switch (m_kinds[index]) {
            case Visit.BodySweep:
                SetBinding(binding: body, index: index);

                return;
            case Visit.RowSweep or Visit.FamilySweep:
                if (m_host.Arena.Keys.TryResolveIndex(index: body, key: out var key)) {
                    MarkKey(index: index, key: key);
                }

                return;
            default:
                MarkFull(index: index);

                return;
        }
    }
    // A move one family member's bindings read whole (a family selection under $member, or the cell under it): every
    // binding of member m. A snapshot that no longer stands names no binding, so the rule is due in full.
    private void MarkMember(int index, int member) {
        if (m_kinds[index] != Visit.FamilySweep) {
            MarkBody(body: member, index: index);

            return;
        }
        if (m_full[index]) {
            return;
        }
        if (!FamilySnapshotStands(index: index)) {
            MarkFull(index: index);

            return;
        }

        var (start, end) = m_familySnapshots[index]!.RangeOf(member: member);

        for (var position = start; (position < end); position++) {
            SetBinding(binding: position, index: index);
        }
    }
    // A move of the cell under one key of member m's row: member m's binding keyed by it. A snapshot that no longer
    // stands names no binding, so the rule is due in full.
    private void MarkMemberKey(int index, int member, CellKey key) {
        if (m_full[index]) {
            return;
        }
        if (!FamilySnapshotStands(index: index)) {
            MarkFull(index: index);

            return;
        }
        if (m_familySnapshots[index]!.TryPositionOf(key: key, member: member, position: out var position)) {
            SetBinding(binding: position, index: index);
        }
    }
    private void SetBinding(int index, int binding) {
        var bindings = m_bindings[index];
        var words = ((binding >> 6) + 1);

        if ((bindings is null) || (bindings.Length < words)) {
            var grown = new ulong[Math.Max(val1: words, val2: ((bindings?.Length ?? 0) * 2))];

            bindings?.CopyTo(array: grown, index: 0);
            m_bindings[index] = bindings = grown;
        }
        BitWords.Add(index: binding, words: bindings);
        SetDue(index: index);
    }
    private bool AnyBinding(int index) {
        if (m_bindings[index] is not { } bindings) {
            return false;
        }
        foreach (var word in bindings) {
            if (word != 0UL) {
                return true;
            }
        }

        return false;
    }
}
