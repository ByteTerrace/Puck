using Puck.Maths;

namespace Puck.State;

/// <summary>One line of the work sheet: a rule or interaction, how many evaluations it can make per tick, what one
/// evaluation costs, and the literal cells its gate pins.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="IsInteraction">Whether the line is an interaction rather than a rule.</param>
/// <param name="Multiplier">The evaluations per tick — one, the <c>forEach</c> row's capacity, or the pair count.</param>
/// <param name="Cost">The orthogonal rule cost components: setup, check, effects.</param>
/// <param name="CheckUnits">The check units: Setup + Multiplier × Check, saturating.</param>
/// <param name="FiringUnits">The firing units: Multiplier × Effects, saturating.</param>
/// <param name="WorkUnits">The line's isolated total: CheckUnits + FiringUnits, saturating.</param>
/// <param name="Discriminators">The literal cells the gate pins, in cell order.</param>
public readonly record struct RuleWorkContributor(
    string Name,
    bool IsInteraction,
    long Multiplier,
    RuleCost Cost,
    long CheckUnits,
    long FiringUnits,
    long WorkUnits,
    IReadOnlyList<RulePinnedCell> Discriminators
) {
    /// <summary>Gets the total unit cost in isolation.</summary>
    public long UnitCost => Cost.Total;
}

/// <summary>One literal cell a gate pins to a closed range; an empty range means the gate can never hold.</summary>
/// <param name="Cell">The cell, as <c>row.key</c>.</param>
/// <param name="Low">The inclusive low end.</param>
/// <param name="High">The inclusive high end.</param>
public readonly record struct RulePinnedCell(string Cell, long Low, long High) {
    /// <summary>Gets a value indicating whether the range admits no value.</summary>
    public bool IsEmpty => (High < Low);
    /// <summary>Gets a value indicating whether the range admits <paramref name="value"/>.</summary>
    public bool Contains(long value) => ((Low <= value) && (value <= High));
    /// <summary>Gets a value indicating whether the two ranges share no value.</summary>
    public bool Disjoint(RulePinnedCell other) => ((High < other.Low) || (other.High < Low));

    /// <summary>Formats the pin as <c>cell=v</c>, <c>cell&lt;=v</c>, <c>cell&gt;=v</c>, <c>cell=a..b</c>, or <c>cell=never</c>.</summary>
    public string Describe() {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (IsEmpty) { return $"{Cell}=never"; }
        if (Low == High) { return $"{Cell}={Low.ToString(provider: culture)}"; }
        if (Low == long.MinValue) { return $"{Cell}<={High.ToString(provider: culture)}"; }
        if (High == long.MaxValue) { return $"{Cell}>={Low.ToString(provider: culture)}"; }

        return $"{Cell}={Low.ToString(provider: culture)}..{High.ToString(provider: culture)}";
    }
}

/// <summary>The static work sheet: the worst-case work units the rules can spend in one tick, tallied so that rules
/// whose gates pin the same literal cells to disjoint ranges share firing costs while all checks still sum.
/// These are heuristic units, not calibrated cycles. A document project builds the contributor lines (it alone knows what a <c>forEach</c> row or a pair
/// co-occurrence multiplies by) and tallies them here.</summary>
public static class RuleWorkBudget {
    /// <summary>Builds one contributor line from a compiled rule.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="multiplier">The evaluations per tick.</param>
    /// <param name="isInteraction">Whether the line is an interaction.</param>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public static RuleWorkContributor Contributor(CompiledRule rule, long multiplier, bool isInteraction, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var cost = rule.CostBreakdown(context: context);
        var checkUnits = SaturatingAdd(left: cost.Setup, right: SaturatingMultiply(left: multiplier, right: cost.Check));
        var firingUnits = SaturatingMultiply(left: multiplier, right: cost.Effects);
        var workUnits = SaturatingAdd(left: checkUnits, right: firingUnits);

        return new RuleWorkContributor(
            Name: rule.Name,
            IsInteraction: isInteraction,
            Multiplier: multiplier,
            Cost: cost,
            CheckUnits: checkUnits,
            FiringUnits: firingUnits,
            WorkUnits: workUnits,
            Discriminators: (isInteraction ? [] : PinnedCells(gate: rule.Gate, contradictory: out _))
        );
    }

    /// <summary>Returns the literal cells a gate pins: a gate that is one comparison, or a top-level conjunction of
    /// comparisons, of a literal-keyed cell against a constant. Ranges on one cell intersect; <c>NotEqual</c> pins
    /// nothing.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="contradictory">Whether some cell's range is empty, so the gate can never hold.</param>
    public static IReadOnlyList<RulePinnedCell> PinnedCells(GateToken[] gate, out bool contradictory) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        contradictory = false;
        if (gate.Length == 0) {
            return [];
        }

        var last = gate[^1];
        var conjuncts = 0;

        if (last.Op == GateOp.Compare) {
            conjuncts = 1;
        } else if ((last.Op == GateOp.All) && (last.Arity == (gate.Length - 1))) {
            conjuncts = last.Arity;
        } else {
            return [];
        }

        var pinned = new SortedDictionary<string, (long Low, long High)>(comparer: StringComparer.Ordinal);

        for (var index = 0; index < conjuncts; index++) {
            var predicate = gate[index];

            if (predicate.Op != GateOp.Compare) {
                return [];
            }
            if ((predicate.Comparand is not null) || (predicate.LeftExpression is not null) || predicate.Left is not StateCellOperand { Key: { } key, KeyFrom: null } state) {
                continue;
            }

            var value = predicate.Value;
            (long Low, long High) range = predicate.Comparison switch {
                ActionStateComparison.Equal => (value, value),
                ActionStateComparison.Less => (long.MinValue, ((value == long.MinValue) ? long.MinValue : (value - 1))),
                ActionStateComparison.LessOrEqual => (long.MinValue, value),
                ActionStateComparison.Greater => (((value == long.MaxValue) ? long.MaxValue : (value + 1)), long.MaxValue),
                ActionStateComparison.GreaterOrEqual => (value, long.MaxValue),
                _ => (long.MinValue, long.MaxValue),
            };

            if ((predicate.Comparison == ActionStateComparison.Less) && (value == long.MinValue)) { range = (1L, 0L); }
            if ((predicate.Comparison == ActionStateComparison.Greater) && (value == long.MaxValue)) { range = (1L, 0L); }
            if (range == (long.MinValue, long.MaxValue)) {
                continue;
            }

            var cell = $"{state.Row}.{key}";

            pinned[cell] = (pinned.TryGetValue(key: cell, value: out var existing)
                ? (Math.Max(val1: existing.Low, val2: range.Low), Math.Min(val1: existing.High, val2: range.High))
                : range);
        }

        var result = new RulePinnedCell[pinned.Count];
        var position = 0;

        foreach (var (cell, (low, high)) in pinned) {
            result[position++] = new RulePinnedCell(Cell: cell, Low: low, High: high);
            contradictory |= (high < low);
        }

        return result;
    }

    /// <summary>Tallies the contributor lines: the evaluation slots (every multiplier summed) and the worst-case
    /// work units. Setup and checks always sum. Each line's firing cost sits at the node of the trie its pinned cells spell, in cell order; a node's worst
    /// case is its own lines plus, per further cell its children pin, the costliest points of that cell — the lines
    /// whose ranges contain one value, summed, taken for one value plus one more per rule evaluation that can write
    /// the cell during the tick, since effects apply immediately and a rule advancing a phase lets the next phase's
    /// rules fire in the same tick. Children pinning different cells are not exclusive and sum.</summary>
    /// <param name="contributors">The lines.</param>
    /// <param name="writers">Per <c>row.key</c>, how many evaluations per tick can write it (<see cref="CountWriters"/>).</param>
    public static (long Slots, long Work) Tally(IReadOnlyList<RuleWorkContributor> contributors, IReadOnlyDictionary<string, long> writers) {
        ArgumentNullException.ThrowIfNull(argument: contributors);
        ArgumentNullException.ThrowIfNull(argument: writers);

        var slots = 0L;
        var checks = 0L;
        var root = new ExclusionNode();

        foreach (var contributor in contributors) {
            slots = SaturatingAdd(left: slots, right: contributor.Multiplier);
            checks = SaturatingAdd(left: checks, right: contributor.CheckUnits);

            var node = root;

            foreach (var pinned in contributor.Discriminators) {
                if (!node.Children.TryGetValue(key: pinned.Cell, value: out var ranges)) {
                    ranges = [];
                    node.Children[pinned.Cell] = ranges;
                }

                var child = default(ExclusionNode);

                foreach (var (range, existing) in ranges) {
                    if ((range.Low == pinned.Low) && (range.High == pinned.High)) {
                        child = existing;
                        break;
                    }
                }
                if (child is null) {
                    child = new ExclusionNode();
                    ranges.Add(item: (pinned, child));
                }

                node = child;
            }

            node.Own = SaturatingAdd(left: node.Own, right: contributor.FiringUnits);
        }

        var effects = Worst(node: root, writers: writers);

        return (slots, SaturatingAdd(left: checks, right: effects));
    }

    /// <summary>Counts, per <c>row.key</c>, how many evaluations per tick can write the cell: each rule's write set
    /// times its multiplier, where a write addressing a row without a literal key counts against every cell of that
    /// row, including every cell any gate pins.</summary>
    /// <param name="rules">The rules with their multipliers.</param>
    public static Dictionary<string, long> CountWriters(IReadOnlyList<(CompiledRule Rule, long Multiplier)> rules) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var byCell = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var byRow = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var (rule, multiplier) in rules) {
            foreach (var write in RuleDataflow.Writes(rule: rule)) {
                var into = ((write.Key is null) ? byRow : byCell);
                var key = ((write.Key is null) ? write.Row : $"{write.Row}.{write.Key}");

                into[key] = SaturatingAdd(left: into.GetValueOrDefault(key: key), right: multiplier);
            }
        }

        var writers = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var (cell, count) in byCell) {
            writers[cell] = SaturatingAdd(left: count, right: byRow.GetValueOrDefault(key: cell[..cell.LastIndexOf(value: '.')]));
        }
        foreach (var (rule, _) in rules) {
            foreach (var pinned in PinnedCells(gate: rule.Gate, contradictory: out _)) {
                if (!writers.ContainsKey(key: pinned.Cell)) {
                    writers[pinned.Cell] = byRow.GetValueOrDefault(key: pinned.Cell[..pinned.Cell.LastIndexOf(value: '.')]);
                }
            }
        }

        return writers;
    }

    /// <summary>Returns the work units a gate costs: each operand and expression read.</summary>
    /// <summary>Returns how many evaluations a rule's <see cref="Rule.ForEach"/> multiplies one tick's cost by: the
    /// iterated row's cell capacity, the zone table's entry count over <c>$zones</c>, or 1 for a rule evaluated once.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public static long ForEachCount(CompiledRule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        if (rule.ForEach is not { } forEach) {
            return 1L;
        }
        if ((rule.Zones is { } zones) && string.Equals(a: forEach, b: RuleFacts.ForEachZones, comparisonType: StringComparison.Ordinal)) {
            return zones.Indices.Length;
        }

        return context.RowCapacity(name: forEach);
    }

    public static long GateCost(GateToken[] tokens, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            if (token.LeftExpression is { } expression) {
                cost = SaturatingAdd(left: cost, right: SaturatingAdd(left: 1L, right: SaturatingAdd(left: ExpressionCost(tokens: expression, kind: token.ValueKind, context: context), right: ExpressionCost(tokens: token.RightExpression!, kind: token.ValueKind, context: context))));
                continue;
            }
            if (token.Left is { } left) { cost = SaturatingAdd(left: cost, right: left.Cost(context: context)); }
            if (token.Comparand is { } comparand) { cost = SaturatingAdd(left: cost, right: comparand.Cost(context: context)); }
        }

        return cost;
    }

    /// <summary>Returns the cost bound of one non-operand operation token.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The expression numeric kind.</param>
    /// <param name="board">The compiled board query for board-shift operations.</param>
    public static CostBound OperationCostBound(ExpressionOp operation, CellKind kind = CellKind.Int, BoardQuery? board = null) =>
        ReferenceSchedule.OperationCostBound(operation: operation, kind: kind, board: board);

    /// <summary>Returns the legacy heuristic work units for a non-operand token, not reference cycles.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="board">The compiled board query for board-shift operations.</param>
    public static long OperationCost(ExpressionOp operation, BoardQuery? board = null) =>
        OperationCost(operation: operation, kind: CellKind.Int, board: board);

    /// <summary>Returns legacy heuristic work units. Numeric kind is carried for future calibration, but the
    /// existing table does not distinguish kinds. Unknown operations receive the rejecting sentinel.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The numeric kind.</param>
    /// <param name="board">The compiled board query for board-shift operations.</param>
    public static long OperationCost(ExpressionOp operation, CellKind kind, BoardQuery? board = null) => operation switch {
        ExpressionOp.Constant or ExpressionOp.Operand => 1L,
        ExpressionOp.BoardShift or ExpressionOp.BoardImage => board is { } b ? b.Topology.CellCount / 2 + b.Visits : 24L,
        ExpressionOp.BoardFill => board is { } fill ? (long)fill.Topology.CellCount * (fill.Topology.CellCount / 2 + fill.Visits) : 1_536L,
        _ => ExpressionOperators.Find(operation)?.Cost ?? long.MaxValue,
    };

    /// <summary>Returns the work units an expression costs: the sum of its operations and live operand reads.</summary>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, RuleCompileContext context) =>
        ExpressionCost(tokens: tokens, kind: CellKind.Int, context: context);

    /// <summary>Returns the work units an expression costs for a specific cell kind: the sum of its operations and live operand reads.</summary>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, CellKind kind, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            var tokenCost = ((token.Operand is { } operand)
                ? operand.Cost(context: context)
                : OperationCost(operation: token.Operation, kind: kind, board: token.Board));
            cost = SaturatingAdd(left: cost, right: tokenCost);
        }

        return cost;
    }

    /// <summary>Returns the work units a list of effects costs.</summary>
    public static long EffectsCost(EffectFact[] effects, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: effects);

        var cost = 0L;

        foreach (var effect in effects) {
            cost = SaturatingAdd(left: cost, right: effect.Cost(context: context));
        }

        return cost;
    }

    /// <summary>Adds, clamping at <see cref="long.MaxValue"/>.</summary>
    public static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right)) ? long.MaxValue : (left + right));
    /// <summary>Multiplies, clamping at <see cref="long.MaxValue"/>.</summary>
    public static long SaturatingMultiply(long left, long right) => ((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right)) ? long.MaxValue : (left * right));

    private sealed class ExclusionNode {
        public long Own { get; set; }
        public Dictionary<string, List<(RulePinnedCell Range, ExclusionNode Child)>> Children { get; } = new(comparer: StringComparer.Ordinal);
    }

    private static long Worst(ExclusionNode node, IReadOnlyDictionary<string, long> writers) {
        var total = node.Own;

        foreach (var (cell, ranges) in node.Children) {
            var worsts = new long[ranges.Count];

            for (var index = 0; index < ranges.Count; index++) { worsts[index] = Worst(node: ranges[index].Child, writers: writers); }

            var sums = new List<long>(capacity: ranges.Count);

            foreach (var (candidate, _) in ranges) {
                if (candidate.IsEmpty) { continue; }

                var sum = 0L;

                for (var index = 0; index < ranges.Count; index++) {
                    if (ranges[index].Range.Contains(value: candidate.Low)) { sum = SaturatingAdd(left: sum, right: worsts[index]); }
                }

                sums.Add(item: sum);
            }

            sums.Sort(comparison: static (left, right) => right.CompareTo(value: left));

            var admitted = (int)Math.Min(val1: sums.Count, val2: SaturatingAdd(left: 1L, right: writers.GetValueOrDefault(key: cell)));

            for (var index = 0; index < admitted; index++) { total = SaturatingAdd(left: total, right: sums[index]); }
        }

        return total;
    }
}
