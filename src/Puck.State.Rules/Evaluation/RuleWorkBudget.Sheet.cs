using System.Globalization;
using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>One literal cell a gate pins to a closed range; an empty range means the gate can never hold.</summary>
/// <param name="RowOrdinal">The row's catalog ordinal.</param>
/// <param name="Key">The cell key.</param>
/// <param name="Low">The inclusive low end.</param>
/// <param name="High">The inclusive high end.</param>
public readonly record struct RulePinnedCell(int RowOrdinal, CellKey Key, long Low, long High) {
    /// <summary>Gets a value indicating whether the range admits no value.</summary>
    public bool IsEmpty => (High < Low);

    /// <summary>Returns whether the range admits a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the range admits it.</returns>
    public bool Contains(long value) => ((Low <= value) && (value <= High));
    /// <summary>Formats the pin as <c>cell=v</c>, <c>cell&lt;=v</c>, <c>cell&gt;=v</c>, <c>cell=a..b</c>, or
    /// <c>cell=never</c>.</summary>
    /// <param name="catalog">The catalog the ordinal and key were minted against.</param>
    /// <returns>The spelling.</returns>
    public string Describe(StateCatalog catalog) {
        var cell = Name(catalog: catalog);

        if (IsEmpty) {
            return $"{cell}=never";
        }
        if (Low == High) {
            return $"{cell}={Low.ToString(provider: CultureInfo.InvariantCulture)}";
        }
        if (Low == long.MinValue) {
            return $"{cell}<={High.ToString(provider: CultureInfo.InvariantCulture)}";
        }
        if (High == long.MaxValue) {
            return $"{cell}>={Low.ToString(provider: CultureInfo.InvariantCulture)}";
        }

        return $"{cell}={Low.ToString(provider: CultureInfo.InvariantCulture)}..{High.ToString(provider: CultureInfo.InvariantCulture)}";
    }
    /// <summary>Returns whether the two ranges share no value.</summary>
    /// <param name="other">The other range.</param>
    /// <returns><see langword="true"/> when they are disjoint.</returns>
    public bool Disjoint(RulePinnedCell other) => ((High < other.Low) || (other.High < Low));
    /// <summary>Formats the pinned cell as <c>row.key</c>.</summary>
    /// <param name="catalog">The catalog the ordinal and key were minted against.</param>
    /// <returns>The name.</returns>
    public string Name(StateCatalog catalog) {
        ArgumentNullException.ThrowIfNull(argument: catalog);

        var row = ((((uint)RowOrdinal) < ((uint)catalog.Descriptors.Count))
            ? catalog.Descriptors[RowOrdinal].Name
            : RowOrdinal.ToString(provider: CultureInfo.InvariantCulture)
        );

        return (catalog.Keys.TryGetName(
            key: Key,
            name: out var key
        )
            ? $"{row}.{key.Value}"
            : $"{row}.*"
        );
    }
}
/// <summary>One line of the work sheet: a rule, how many evaluations it can make per tick, what one evaluation
/// costs, and the literal cells its gate pins.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="IsInteraction">Whether the line is an interaction rather than a rule.</param>
/// <param name="Multiplier">The evaluations per tick — one, the iterated row's capacity, or the pair count.</param>
/// <param name="Cost">The orthogonal rule cost components: setup, check, effects.</param>
/// <param name="CheckUnits">The check units: Setup + Multiplier x Check.</param>
/// <param name="FiringUnits">The firing units: Firings x Effects.</param>
/// <param name="WorkUnits">The line's isolated total: CheckUnits + FiringUnits.</param>
/// <param name="Discriminators">The literal cells the gate pins, in cell order.</param>
/// <param name="Firings">The most evaluations per tick that can fire: the multiplier, or the rule's
/// <see cref="CompiledRule.MaxFirings"/> when that is smaller.</param>
/// <param name="Order">The line's place in the tick's one evaluation pass, ascending as the pass reaches it, or a
/// negative value when the line may run anywhere in the tick or more than once (<see cref="RuleCellWriters"/>).</param>
public readonly record struct RuleWorkContributor(
    string Name,
    bool IsInteraction,
    long Multiplier,
    RuleCost Cost,
    RuleWork CheckUnits,
    RuleWork FiringUnits,
    RuleWork WorkUnits,
    IReadOnlyList<RulePinnedCell> Discriminators,
    long Firings,
    long Order = -1L
);
/// <summary>The firings per tick that can write one cell, each at its place in the tick's evaluation pass. A write
/// changes what the lines after it read and nothing before, so two lines can read different values of the cell only
/// when a write lands between them: one at or after the earlier line's place and before the later one's. A writer
/// whose place is negative may run anywhere and counts between every pair.</summary>
public sealed class RuleCellWriters {
    private readonly List<(long Order, long Firings)> m_writers = [];

    /// <summary>Gets every firing per tick that can write the cell, wherever it runs.</summary>
    public long Total { get; private set; }

    /// <summary>Adds the firings of one writer.</summary>
    /// <param name="order">The writer's place in the pass, or a negative value for anywhere.</param>
    /// <param name="firings">The firings per tick the writer can make.</param>
    public void Add(long order, long firings) {
        m_writers.Add(item: (order, firings));
        Total = BinaryIntegerFunctions.AddSaturating(left: Total, right: firings);
    }
    /// <summary>Adds every writer of another cell's set, such as a write whose key reaches every cell of a row.</summary>
    /// <param name="other">The writers to add.</param>
    public void AddAll(RuleCellWriters other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        foreach (var (order, firings) in other.m_writers) {
            Add(firings: firings, order: order);
        }
    }
    /// <summary>Returns the firings that can land between lines placed from <paramref name="low"/> through
    /// <paramref name="high"/>: those at or after the first and before the last, and every one placed anywhere.</summary>
    /// <param name="low">The earliest line's place.</param>
    /// <param name="high">The latest line's place.</param>
    /// <returns>The firings.</returns>
    public long Between(long low, long high) {
        var count = 0L;

        foreach (var (order, firings) in m_writers) {
            if ((order < 0L) || ((low <= order) && (order < high))) {
                count = BinaryIntegerFunctions.AddSaturating(left: count, right: firings);
            }
        }

        return count;
    }
}
public static partial class RuleWorkBudget {
    private sealed class ExclusionNode {
        public Dictionary<(int RowOrdinal, CellKey Key), List<(RulePinnedCell Range, ExclusionNode Child)>> Children { get; } = [];
        public RuleWork Own { get; set; } = RuleWork.Zero;
        // The places of the lines at or below this node: the earliest and latest, and whether one may run anywhere.
        public long Low { get; set; } = long.MaxValue;
        public long High { get; set; } = long.MinValue;
        public bool Anywhere { get; set; }

        public void Place(long order) {
            if (order < 0L) {
                Anywhere = true;

                return;
            }
            Low = Math.Min(val1: Low, val2: order);
            High = Math.Max(val1: High, val2: order);
        }
    }

    private static RuleWork Worst(ExclusionNode node, IReadOnlyDictionary<(int RowOrdinal, CellKey Key), RuleCellWriters> writers) {
        var total = node.Own;

        foreach (var (cell, ranges) in node.Children) {
            var worsts = new RuleWork[ranges.Count];
            var low = long.MaxValue;
            var high = long.MinValue;
            var anywhere = false;

            for (var index = 0; (index < ranges.Count); index++) {
                var child = ranges[index].Child;

                worsts[index] = Worst(
                    node: child,
                    writers: writers
                );
                low = Math.Min(val1: low, val2: child.Low);
                high = Math.Max(val1: high, val2: child.High);
                anywhere |= child.Anywhere;
            }

            var sums = new List<RuleWork>(capacity: ranges.Count);

            foreach (var (candidate, _) in ranges) {
                if (candidate.IsEmpty) {
                    continue;
                }

                var sum = RuleWork.Zero;

                for (var index = 0; (index < ranges.Count); index++) {
                    if (ranges[index].Range.Contains(value: candidate.Low)) {
                        sum += worsts[index];
                    }
                }

                sums.Add(item: sum);
            }

            // Costliest first, an unpriced or overflowed point ahead of every known one, so the points admitted are
            // never the cheap ones.
            sums.Sort(comparison: static (left, right) => RuleWork.Compare(
                left: right,
                right: left
            ));

            // The cell reads one value at the first of these lines, and one more for each write that can land between
            // the first and the last of them.
            var between = (writers.TryGetValue(key: cell, value: out var cellWriters)
                ? (anywhere ? cellWriters.Total : cellWriters.Between(high: high, low: low))
                : 0L);
            var admitted = ((int)Math.Min(
                val1: sums.Count,
                val2: BinaryIntegerFunctions.AddSaturating(
                    left: 1L,
                    right: between
                )
            ));

            for (var index = 0; (index < admitted); index++) {
                total += sums[index];
            }
        }

        return total;
    }

    /// <summary>Builds one contributor line from a compiled rule.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="multiplier">The evaluations per tick.</param>
    /// <param name="isInteraction">Whether the line is an interaction.</param>
    /// <param name="context">The context the rule was priced against.</param>
    /// <param name="commit">The work the host's commit of one firing spends beyond what the effects price, added to
    /// the line's per-firing effects.</param>
    /// <param name="order">The line's place in the tick's evaluation pass, or a negative value when it may run anywhere
    /// or more than once (<see cref="RuleWorkContributor.Order"/>).</param>
    /// <returns>The line.</returns>
    public static RuleWorkContributor Contributor(CompiledRule rule, long multiplier, bool isInteraction, IRuleCostContext context, long commit = 0L, long order = -1L) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var breakdown = rule.CostBreakdown(context: context);
        var cost = breakdown with { Effects = (breakdown.Effects + commit) };
        var firings = Firings(multiplier: multiplier, rule: rule);
        var checkUnits = (cost.Setup + (multiplier * cost.Check));
        var firingUnits = (firings * cost.Effects);

        return new RuleWorkContributor(
            CheckUnits: checkUnits,
            Cost: cost,
            Discriminators: (isInteraction
            ? []
            : PinnedCells(
                    contradictory: out _,
                    gate: rule.Gate
                )),
            Firings: firings,
            FiringUnits: firingUnits,
            IsInteraction: isInteraction,
            Multiplier: multiplier,
            Name: rule.Name,
            Order: order,
            WorkUnits: (checkUnits + firingUnits)
        );
    }
    /// <summary>Returns how many of a rule's evaluations per tick can fire: its multiplier, bounded by the rule's
    /// <see cref="CompiledRule.MaxFirings"/>, which the evaluator enforces by ending the sweep.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="multiplier">The rule's evaluations per tick.</param>
    /// <returns>The firings.</returns>
    public static long Firings(CompiledRule rule, long multiplier) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        return ((rule.MaxFirings > 0)
            ? Math.Min(val1: multiplier, val2: rule.MaxFirings)
            : multiplier
        );
    }
    /// <summary>Counts, per cell, the evaluations per tick that can write it and where in the pass each runs: each
    /// rule's write set times the firings its multiplier allows (<see cref="Firings"/>), at the rule's place, where a
    /// write addressing a row without a literal key counts against every cell of that row, including every cell any
    /// gate pins.</summary>
    /// <param name="rules">The rules with their multipliers and their places in the tick's pass (negative for a rule
    /// that may run anywhere or more than once), as <see cref="RuleWorkContributor.Order"/> places their lines.</param>
    /// <returns>The writers, keyed by (row ordinal, key ordinal).</returns>
    public static Dictionary<(int RowOrdinal, CellKey Key), RuleCellWriters> CountWriters(IReadOnlyList<(CompiledRule Rule, long Multiplier, long Order)> rules) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var writers = new Dictionary<(int RowOrdinal, CellKey Key), RuleCellWriters>();
        var byRow = new Dictionary<int, RuleCellWriters>();
        var writes = new List<CellAccess>();

        foreach (var (rule, evaluations, order) in rules) {
            var multiplier = Firings(multiplier: evaluations, rule: rule);

            writes.Clear();
            rule.CollectWrites(into: writes);

            foreach (var write in writes) {
                if (write.Key.IsValid) {
                    var cell = (write.RowOrdinal, write.Key);

                    if (!writers.TryGetValue(key: cell, value: out var cellWriters)) {
                        writers[cell] = cellWriters = new RuleCellWriters();
                    }
                    cellWriters.Add(firings: multiplier, order: order);
                } else {
                    if (!byRow.TryGetValue(key: write.RowOrdinal, value: out var rowWriters)) {
                        byRow[write.RowOrdinal] = rowWriters = new RuleCellWriters();
                    }
                    rowWriters.Add(firings: multiplier, order: order);
                }
            }
        }
        foreach (var (rule, _, _) in rules) {
            foreach (var pinned in PinnedCells(
                contradictory: out _,
                gate: rule.Gate
            )) {
                var cell = (pinned.RowOrdinal, pinned.Key);

                if (!writers.ContainsKey(key: cell)) {
                    writers[cell] = new RuleCellWriters();
                }
            }
        }
        foreach (var (cell, cellWriters) in writers) {
            if (byRow.TryGetValue(key: cell.RowOrdinal, value: out var rowWriters)) {
                cellWriters.AddAll(other: rowWriters);
            }
        }

        return writers;
    }
    /// <summary>Returns how many evaluations one tick's cost is multiplied by: the iterated row's cell capacity, the
    /// zone table's entry count, the document's body capacity for a body sweep, the pool's capacity for a pool sweep, or
    /// 1 for a rule evaluated once.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="context">The context the rule was resolved against.</param>
    /// <returns>The multiplier.</returns>
    public static long ForEachCount(CompiledRule rule, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        if (rule.Iteration is { } iteration) { return iteration.Capacity; }
        if (rule.ForEachBodies) {
            return context.BodyCapacity;
        }
        if (rule.ForEachZones) {
            return (rule.Zones?.Indices.Length ?? 1);
        }
        if (rule.PoolForEach is { } pool) {
            return pool.Capacity;
        }
        if (rule.ForEachOrdinal < 0) {
            return 1L;
        }

        return context.RowCapacity(rowOrdinal: rule.ForEachOrdinal);
    }
    /// <summary>Returns what one sweep of a rule costs before its first evaluation: one unit per key the sweep
    /// snapshots, which is the iterated row's capacity, the zone table's entry count, the body capacity or the pool's capacity, and nothing for a rule
    /// evaluated once.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="context">The context the rule was resolved against.</param>
    /// <returns>The setup work units.</returns>
    public static RuleWork ForEachSetup(CompiledRule rule, IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        return (((rule.ForEachOrdinal < 0) && !rule.ForEachZones && !rule.ForEachBodies && (rule.Iteration is null) && (rule.PoolForEach is null))
            ? RuleWork.Zero
            : RuleWork.Known(units: ForEachCount(
                context: context,
                rule: rule
            ))
        );
    }
    /// <summary>Returns the literal cells a gate pins: a gate that is one comparison, or a top-level conjunction of
    /// comparisons, of a literal-keyed cell against a constant. Ranges on one cell intersect; <c>NotEqual</c> pins
    /// nothing.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="contradictory">Whether some cell's range is empty, so the gate can never hold.</param>
    /// <returns>The pins, in cell order.</returns>
    public static IReadOnlyList<RulePinnedCell> PinnedCells(GateToken[] gate, out bool contradictory) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        if (Array.Exists(array: gate, match: static token => (token.Op == GateOp.ShortCircuit))) {
            gate = Array.FindAll(array: gate, match: static token => (token.Op != GateOp.ShortCircuit));
        }

        contradictory = false;

        if (gate.Length == 0) {
            return [];
        }

        var last = gate[^1];
        var conjuncts = 0;

        if (last.Op == GateOp.Compare) {
            conjuncts = 1;
        } else if (
            (last.Op == GateOp.All) &&
            (last.Arity == (gate.Length - 1))
        ) {
            conjuncts = last.Arity;
        } else {
            return [];
        }

        var pinned = new Dictionary<(int RowOrdinal, CellKey Key), (long Low, long High)>();

        for (var index = 0; (index < conjuncts); index++) {
            var predicate = gate[index];

            if (predicate.Op != GateOp.Compare) {
                return [];
            }
            if (
                !predicate.RightSource.IsLiteral ||
                predicate.LeftSource.IsExpression ||
                (predicate.LeftSource.Operand is not StateCellOperand { KeyFrom: null, RowFrom: null } state) ||
                !state.Key.IsValid
            ) {
                continue;
            }

            var value = predicate.RightSource.RawValue;
            (long Low, long High) range = (predicate.Comparison switch {
                ExpressionOp.Equal => (value, value),
                ExpressionOp.Less => (long.MinValue, ((value == long.MinValue)
                ? long.MinValue
                : (value - 1L))),
                ExpressionOp.LessOrEqual => (long.MinValue, value),
                ExpressionOp.Greater => (((value == long.MaxValue)
                ? long.MaxValue
                : (value + 1L)), long.MaxValue),
                ExpressionOp.GreaterOrEqual => (value, long.MaxValue),
                _ => (long.MinValue, long.MaxValue),
            });

            if (
                (predicate.Comparison == ExpressionOp.Less) &&
                (value == long.MinValue)
            ) {
                range = (1L, 0L);
            }
            if (
                (predicate.Comparison == ExpressionOp.Greater) &&
                (value == long.MaxValue)
            ) {
                range = (1L, 0L);
            }
            if (range == (long.MinValue, long.MaxValue)) {
                continue;
            }

            var cell = (state.RowOrdinal, state.Key);

            pinned[cell] = (pinned.TryGetValue(
                key: cell,
                value: out var existing
            )
                ? (Math.Max(
                    val1: existing.Low,
                    val2: range.Low
                ), Math.Min(
                    val1: existing.High,
                    val2: range.High
                ))
                : range
            );
        }

        var result = new RulePinnedCell[pinned.Count];
        var position = 0;

        foreach (var (cell, (low, high)) in pinned) {
            result[position++] = new RulePinnedCell(
                High: high,
                Key: cell.Key,
                Low: low,
                RowOrdinal: cell.RowOrdinal
            );
            contradictory |= (high < low);
        }

        // Cell order, so two compilations of the same gate produce the same discriminator list.
        Array.Sort(
            array: result,
            comparison: static (left, right) => {
                return LexicographicOrder.Compare(
                    leftPrimary: left.RowOrdinal,
                    leftSecondary: left.Key.Ordinal,
                    rightPrimary: right.RowOrdinal,
                    rightSecondary: right.Key.Ordinal
                );
            }
        );

        return result;
    }
    /// <summary>Tallies the contributor lines: the evaluation slots and the worst-case work units. Setup and checks
    /// always sum. Each line's firing cost sits at the node of the trie its pinned cells spell; a node's worst case
    /// is its own lines plus, per further cell its children pin, the costliest points of that cell, taken for one
    /// value plus one more per evaluation that can write the cell between the first and the last of those lines in
    /// the tick's pass (every writer, when one of the lines may run anywhere). Children pinning different cells are
    /// not exclusive and sum.</summary>
    /// <param name="contributors">The lines.</param>
    /// <param name="writers">Per cell, the evaluations per tick that can write it and their places
    /// (<see cref="CountWriters"/>).</param>
    /// <returns>The evaluation slots and the worst-case work units.</returns>
    public static (long Slots, RuleWork Work) Tally(IReadOnlyList<RuleWorkContributor> contributors, IReadOnlyDictionary<(int RowOrdinal, CellKey Key), RuleCellWriters> writers) {
        ArgumentNullException.ThrowIfNull(argument: contributors);
        ArgumentNullException.ThrowIfNull(argument: writers);

        var checks = RuleWork.Zero;
        var root = new ExclusionNode();
        var slots = 0L;

        foreach (var contributor in contributors) {
            slots = BinaryIntegerFunctions.AddSaturating(
                left: slots,
                right: contributor.Multiplier
            );
            checks += contributor.CheckUnits;

            var node = root;

            node.Place(order: contributor.Order);
            foreach (var pinned in contributor.Discriminators) {
                var cell = (pinned.RowOrdinal, pinned.Key);

                if (!node.Children.TryGetValue(
                    key: cell,
                    value: out var ranges
                )) {
                    ranges = [];
                    node.Children[cell] = ranges;
                }

                var child = default(ExclusionNode);

                foreach (var (range, existing) in ranges) {
                    if (
                        (range.Low == pinned.Low) &&
                        (range.High == pinned.High)
                    ) {
                        child = existing;

                        break;
                    }
                }

                if (child is null) {
                    child = new ExclusionNode();
                    ranges.Add(item: (pinned, child));
                }

                node = child;
                node.Place(order: contributor.Order);
            }

            node.Own += contributor.FiringUnits;
        }

        return (slots, (checks + Worst(
            node: root,
            writers: writers
        )));
    }
}
