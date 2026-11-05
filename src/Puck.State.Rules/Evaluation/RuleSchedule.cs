using System.Text;

namespace Puck.State.Rules;

/// <summary>Why a schedule cannot prove an evaluation's reads unchanged from versions alone.</summary>
[Flags]
public enum RuleVolatility : byte {
    /// <summary>Every read is named and versioned.</summary>
    None = 0,

    /// <summary>The reads include the tick pair, which moves every tick with no state write.</summary>
    ReadsTick = 1,

    /// <summary>The reads include the search ply, which a search host moves with no state write.</summary>
    ReadsSearchPly = 2,

    /// <summary>The reads include a host fact the host advertises no version for.</summary>
    HostFacet = 4,

    /// <summary>The reads include a row the host's own storage serves instead of the arena.</summary>
    HostOwnedRow = 8,

    /// <summary>The reads include a row carrying a value-over-time trait, whose live value moves with the tick.</summary>
    TraitRow = 16,

    /// <summary>The reads include a row the layout does not carry.</summary>
    UnknownRow = 32,
}
/// <summary>A rule's or a binding's memoized read schedule: every dependency one judgement can have — the arena rows
/// its locals, live-row selection and gate read, and the host facts they read, each versioned — and, for a sweep, the
/// dependencies of the set of bindings it sweeps. A schedule names a dependency or is volatile: an elided or reused
/// evaluation is admitted only when the schedule is not volatile and every dependency's version is the one captured
/// with the verdict (<see cref="RuleEvaluator"/>'s one door). Built once per compiled rule, evaluator and host fact
/// epoch, and cached in the rule's latch state; never hashed.</summary>
/// <param name="Rows">The distinct catalog row ordinals a judgement reads, ascending.</param>
/// <param name="Facts">The distinct host facts a judgement reads, in walk order.</param>
/// <param name="Volatility">Why versions alone cannot prove the reads unchanged; <see cref="RuleVolatility.None"/>
/// when they can.</param>
/// <param name="UnprovenRows">The rows that made the schedule volatile — a trait row, a host-owned row, or an
/// ordinal the layout does not carry — ascending, for the read-back.</param>
/// <param name="UnversionedFacts">The kinds of the host facts no version names, for the read-back.</param>
/// <param name="IterationRows">The rows whose version moves when a sweep's binding set changes: the keyed row a sweep
/// iterates.</param>
/// <param name="IterationFacts">The host sources whose version moves when a sweep's binding set changes: the live-body
/// roster a body sweep iterates.</param>
/// <param name="IterationVersioned">Whether the rule's binding set is one these dependencies name: a rule evaluated
/// once, a keyed-row sweep, or a body sweep over a versioned roster. A pool, zone or document-owned sweep is not, and
/// a due list visits it every tick; its bindings' own skip is unaffected.</param>
/// <param name="Deadline">The gate's deadline conjunct, whose tick read is the wake a closed verdict carries rather
/// than a read that makes the schedule volatile, or <see langword="null"/>.</param>
public sealed record RuleSchedule(int[] Rows, FactRead[] Facts, RuleVolatility Volatility, int[] UnprovenRows, string[] UnversionedFacts, int[] IterationRows, FactRead[] IterationFacts, bool IterationVersioned, RuleDeadline? Deadline = null) {
    /// <summary>Gets a value indicating whether versions alone cannot prove the reads unchanged.</summary>
    public bool Volatile => (Volatility != RuleVolatility.None);
    /// <summary>Gets how many versions one capture of the schedule holds: one per row, then one per fact.</summary>
    public int Dependencies => (Rows.Length + Facts.Length);
    /// <summary>Gets a value indicating whether any host fact is read for the body bound to <c>$each</c>.</summary>
    public bool ReadsEachBody => Array.Exists(array: Facts, match: static fact => (fact.Body == FactRead.EachBody));

    /// <summary>Builds a schedule from what one judgement observes.</summary>
    /// <param name="reads">The rows the judgement reads.</param>
    /// <param name="needs">The judgement's needs: whether it reads the tick or the search ply.</param>
    /// <param name="facts">The facet-bearing facts the judgement reads, each asked of the host
    /// (<see cref="IStateReader.TryScheduleFact"/>).</param>
    /// <param name="reader">The host, which names the facts' dependencies and whose layout the trait test reads.</param>
    /// <param name="iterationRow">The keyed row a sweep iterates, or -1.</param>
    /// <param name="iteratesBodies">Whether the rule sweeps the live bodies.</param>
    /// <param name="iterationVersioned">Whether the rule's binding set is one a version can name: no sweep, a keyed
    /// row, or the live bodies.</param>
    /// <param name="deadline">The gate's deadline conjunct, whose tick side <paramref name="reads"/> and
    /// <paramref name="needs"/> leave out, or <see langword="null"/>.</param>
    /// <returns>The schedule.</returns>
    public static RuleSchedule Build(IReadOnlyList<CellAccess> reads, RuleNeeds needs, IReadOnlyList<ICompiledFact> facts, IStateReader reader, int iterationRow = -1, bool iteratesBodies = false, bool iterationVersioned = true, RuleDeadline? deadline = null) {
        ArgumentNullException.ThrowIfNull(argument: needs);
        ArgumentNullException.ThrowIfNull(argument: facts);
        ArgumentNullException.ThrowIfNull(argument: reader);
        ArgumentNullException.ThrowIfNull(argument: reads);

        var volatility = RuleVolatility.None;

        if (needs.ReadsTick) {
            volatility |= RuleVolatility.ReadsTick;
        }
        if (needs.ReadsSearchPly && !reader.SearchPlyIsFixed) {
            volatility |= RuleVolatility.ReadsSearchPly;
        }

        var layout = reader.Arena.Layout;
        var descriptors = reader.Catalog.Descriptors;
        var seen = new SortedSet<int>();
        var unproven = new SortedSet<int>();
        var factRows = new List<int>();
        var factReads = new List<FactRead>();
        var unversioned = new List<string>();

        foreach (var row in needs.HostOwnedRows) {
            volatility |= RuleVolatility.HostOwnedRow;
            _ = unproven.Add(item: row);
        }
        foreach (var fact in facts) {
            var rowsBefore = factRows.Count;
            var factsBefore = factReads.Count;

            if (reader.TryScheduleFact(fact: fact, rows: factRows, facts: factReads)) {
                continue;
            }

            CollectionsTrim(list: factRows, count: rowsBefore);
            CollectionsTrim(list: factReads, count: factsBefore);
            volatility |= RuleVolatility.HostFacet;

            var kind = Kind(fact: fact);

            if (!unversioned.Contains(item: kind)) {
                unversioned.Add(item: kind);
            }
        }

        void Row(int ordinal) {
            if (((uint)ordinal) >= ((uint)layout.RowCount)) {
                // A read whose row the layout does not carry is unprovable rather than absent: whatever answers it
                // answers outside the arena's versions.
                volatility |= RuleVolatility.UnknownRow;
                _ = unproven.Add(item: ordinal);

                return;
            }

            // A row whose slot or any declared cell carries a value-over-time trait computes a different live value
            // every tick from the same stored bits, so an unchanged version proves nothing about a read of it; a
            // host-owned row answers from storage no row version covers.
            if (layout[ordinal].HasTraits) {
                volatility |= RuleVolatility.TraitRow;
                _ = unproven.Add(item: ordinal);
            }
            if ((((uint)ordinal) < ((uint)descriptors.Count)) && descriptors[ordinal].HostOwned) {
                volatility |= RuleVolatility.HostOwnedRow;
                _ = unproven.Add(item: ordinal);
            }
            _ = seen.Add(item: ordinal);
        }

        foreach (var access in reads) {
            Row(ordinal: access.RowOrdinal);
        }
        foreach (var ordinal in factRows) {
            Row(ordinal: ordinal);
        }

        var iterationRows = new List<int>(capacity: 1);
        var iterationFacts = new List<FactRead>(capacity: 1);

        var versionedIteration = (iterationVersioned && (!iteratesBodies || reader.TryScheduleLiveBodies(facts: iterationFacts)));

        if (iterationRow >= 0) {
            iterationRows.Add(item: iterationRow);
        }

        return new RuleSchedule(
            Deadline: deadline,
            Facts: [.. factReads.Distinct()],
            IterationFacts: [.. iterationFacts],
            IterationRows: [.. iterationRows],
            IterationVersioned: versionedIteration,
            Rows: [.. seen],
            UnprovenRows: [.. unproven],
            UnversionedFacts: [.. unversioned],
            Volatility: volatility
        );
    }
    /// <summary>Spells the schedule for a read-back: the rows and host facts it depends on by name, and every reason it
    /// is volatile.</summary>
    /// <param name="reader">The reader whose catalog names the rows and which names its fact sources.</param>
    /// <returns>The spelling, such as <c>rows goCell,goMark; facts none; volatile: reads $tick</c>.</returns>
    public string Describe(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var text = new StringBuilder();

        text.Append(value: "rows ");
        AppendRows(reader: reader, rows: Rows, text: text);
        text.Append(value: "; facts ");
        if (Facts.Length == 0) {
            text.Append(value: "none");
        }
        for (var index = 0; (index < Facts.Length); index++) {
            text.Append(value: ((index > 0) ? "," : string.Empty)).Append(value: DescribeFact(reader: reader, fact: Facts[index]));
        }
        if ((IterationRows.Length > 0) || (IterationFacts.Length > 0)) {
            text.Append(value: "; sweeps ");
            AppendRows(reader: reader, rows: IterationRows, text: text, none: false);
            foreach (var fact in IterationFacts) {
                text.Append(value: ((IterationRows.Length > 0) ? "," : string.Empty)).Append(value: DescribeFact(reader: reader, fact: fact));
            }
        }
        if (!IterationVersioned) {
            text.Append(value: "; sweeps a set no version names");
        }
        if (Deadline is { } deadline) {
            text.Append(value: $"; wakes at its {RuleFacts.Tick} deadline (gate conjunct {deadline.Token})");
        }
        if (!Volatile) {
            return text.Append(value: "; versioned").ToString();
        }

        text.Append(value: "; volatile:");

        var first = true;

        void Reason(string reason) {
            text.Append(value: (first ? " " : ", ")).Append(value: reason);
            first = false;
        }

        if (Volatility.HasFlag(flag: RuleVolatility.ReadsTick)) {
            Reason(reason: $"reads {RuleFacts.Tick}");
        }
        if (Volatility.HasFlag(flag: RuleVolatility.ReadsSearchPly)) {
            Reason(reason: $"reads {RuleFacts.SearchPly}");
        }
        foreach (var kind in UnversionedFacts) {
            Reason(reason: $"fact {kind} has no version");
        }
        foreach (var row in UnprovenRows) {
            var name = RowName(reader: reader, rowOrdinal: row);

            Reason(reason: ((((uint)row) >= ((uint)reader.Arena.Layout.RowCount))
                ? $"row {name} is not in the layout"
                : (reader.Arena.Layout[row].HasTraits
                    ? $"row {name} carries a trait"
                    : $"row {name} is host-owned")));
        }

        return text.ToString();
    }

    private static string DescribeFact(IStateReader reader, FactRead fact) => $"{reader.DescribeFactSource(source: fact.Source)}({fact.Body switch {
        FactRead.EachBody => "$each",
        FactRead.Whole => "*",
        _ => fact.Body.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
    }})";
    // A fact's read-back name: its own spelling when it gives one, otherwise its compiled type without the operand or
    // fact suffix.
    private static string Kind(ICompiledFact fact) {
        var spelled = fact.ToString();

        if (!string.IsNullOrEmpty(value: spelled) && !string.Equals(a: spelled, b: fact.GetType().ToString(), comparisonType: StringComparison.Ordinal)) {
            return spelled;
        }

        var name = fact.GetType().Name;

        foreach (var suffix in (ReadOnlySpan<string>)["Operand", "Fact"]) {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.Ordinal) && (name.Length > suffix.Length)) {
                return name[..^suffix.Length];
            }
        }

        return name;
    }
    private static void CollectionsTrim<T>(List<T> list, int count) {
        if (list.Count > count) {
            list.RemoveRange(index: count, count: (list.Count - count));
        }
    }
    private static void AppendRows(IStateReader reader, int[] rows, StringBuilder text, bool none = true) {
        if (rows.Length == 0) {
            if (none) {
                text.Append(value: "none");
            }

            return;
        }
        for (var index = 0; (index < rows.Length); index++) {
            if (index > 0) {
                text.Append(value: ',');
            }
            text.Append(value: RowName(reader: reader, rowOrdinal: rows[index]));
        }
    }
    private static string RowName(IStateReader reader, int rowOrdinal) {
        var descriptors = reader.Catalog.Descriptors;

        return ((((uint)rowOrdinal) < ((uint)descriptors.Count))
            ? descriptors[rowOrdinal].Name
            : $"#{rowOrdinal}");
    }
}
