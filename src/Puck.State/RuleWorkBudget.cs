namespace Puck.State;

/// <summary>One line of the work sheet: a rule or interaction, how many evaluations it can make per tick, what one
/// evaluation costs, and the literal cells its gate pins.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="IsInteraction">Whether the line is an interaction rather than a rule.</param>
/// <param name="Multiplier">The evaluations per tick — one, the <c>forEach</c> row's capacity, or the pair count.</param>
/// <param name="UnitCost">The work units one evaluation costs.</param>
/// <param name="WorkUnits">The line's total: <paramref name="Multiplier"/> × <paramref name="UnitCost"/>, saturating.</param>
/// <param name="Discriminators">The literal cells the gate pins, in cell order.</param>
public readonly record struct RuleWorkContributor(string Name, bool IsInteraction, long Multiplier, long UnitCost, long WorkUnits, IReadOnlyList<RulePinnedCell> Discriminators);

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
/// whose gates pin the same literal cells to disjoint ranges are charged their costliest members rather than their
/// sum. A document project builds the contributor lines (it alone knows what a <c>forEach</c> row or a pair
/// co-occurrence multiplies by) and tallies them here.</summary>
public static class RuleWorkBudget {
    /// <summary>Builds one contributor line from a compiled rule.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="multiplier">The evaluations per tick.</param>
    /// <param name="isInteraction">Whether the line is an interaction.</param>
    /// <param name="context">The compile context the rule was resolved against.</param>
    public static RuleWorkContributor Contributor(CompiledRule rule, long multiplier, bool isInteraction, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var unit = rule.Cost(context: context);

        return new RuleWorkContributor(
            Name: rule.Name,
            IsInteraction: isInteraction,
            Multiplier: multiplier,
            UnitCost: unit,
            WorkUnits: SaturatingMultiply(left: multiplier, right: unit),
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
    /// work units. Each line sits at the node of the trie its pinned cells spell, in cell order; a node's worst
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
        var root = new ExclusionNode();

        foreach (var contributor in contributors) {
            slots = SaturatingAdd(left: slots, right: contributor.Multiplier);

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

            node.Own = SaturatingAdd(left: node.Own, right: contributor.WorkUnits);
        }

        return (slots, Worst(node: root, writers: writers));
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
    public static long GateCost(GateToken[] tokens, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            if (token.LeftExpression is { } expression) {
                cost = SaturatingAdd(left: cost, right: SaturatingAdd(left: 1L, right: SaturatingAdd(left: ExpressionCost(tokens: expression, context: context), right: ExpressionCost(tokens: token.RightExpression!, context: context))));
                continue;
            }
            if (token.Left is { } left) { cost = SaturatingAdd(left: cost, right: left.Cost(context: context)); }
            if (token.Comparand is { } comparand) { cost = SaturatingAdd(left: cost, right: comparand.Cost(context: context)); }
        }

        return cost;
    }

    /// <summary>Returns the work units one non-operand operation token costs based on its static disassembly and execution complexity.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="board">The compiled board query for board-shift operations.</param>
    public static long OperationCost(ExpressionOp operation, BoardQuery? board = null) => operation switch {
        // Tier 0: Single-cycle ALU, bitwise ops, shifts, relations, unary math (1)
        ExpressionOp.Constant or ExpressionOp.Operand
            or ExpressionOp.Add or ExpressionOp.Subtract
            or ExpressionOp.BitAnd or ExpressionOp.BitOr or ExpressionOp.BitXor or ExpressionOp.BitNot
            or ExpressionOp.ShiftLeft or ExpressionOp.ShiftRight or ExpressionOp.ShiftRightLogical
            or ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.Greater
            or ExpressionOp.LessOrEqual or ExpressionOp.GreaterOrEqual
            or ExpressionOp.Negate or ExpressionOp.Abs or ExpressionOp.Sign => 1L,

        // Tier 1: Conditional choice & select (2)
        ExpressionOp.Minimum or ExpressionOp.Maximum or ExpressionOp.Clamp or ExpressionOp.Select => 2L,

        // Tier 2: Dedicated hardware bit intrinsics (2)
        ExpressionOp.PopCount or ExpressionOp.LeadingZeroCount or ExpressionOp.TrailingZeroCount
            or ExpressionOp.LowestSetBit or ExpressionOp.ClearLowestSetBit
            or ExpressionOp.ByteSwap or ExpressionOp.BitReverse or ExpressionOp.SmallestMissing => 2L,

        // Tier 3: Hardware multiply, rotations, bitfields, parallel bit extract/deposit (3-4)
        ExpressionOp.Multiply or ExpressionOp.RotateLeft or ExpressionOp.RotateRight
            or ExpressionOp.BitField or ExpressionOp.BitInsert => 3L,
        ExpressionOp.ParallelBitExtract or ExpressionOp.ParallelBitDeposit => 4L,

        // Tier 4: Fast coordinate projections (5-6)
        ExpressionOp.HexEuclideanSquared or ExpressionOp.SquareRadius or ExpressionOp.SquareLength
            or ExpressionOp.SquareEuclideanSquared => 5L,
        ExpressionOp.SquareDistance or ExpressionOp.SquareChebyshev => 6L,

        // Tier 5: Direct coordinate algebra (10-15)
        ExpressionOp.HexDistance or ExpressionOp.HexNeighbor or ExpressionOp.HexRotate
            or ExpressionOp.HexMirror or ExpressionOp.HexSwap or ExpressionOp.HexAdd or ExpressionOp.HexSubtract
            or ExpressionOp.SquareIndex or ExpressionOp.SquareNeighbor or ExpressionOp.SquareRotate
            or ExpressionOp.SquareMirror or ExpressionOp.SquareSwap or ExpressionOp.SquareAdd or ExpressionOp.SquareSubtract
            or ExpressionOp.SquareMultiply or ExpressionOp.SquareScale or ExpressionOp.SquareTranslate => 10L,
        ExpressionOp.Pair or ExpressionOp.PairTranslate or ExpressionOp.PairScale
            or ExpressionOp.HexScale or ExpressionOp.HexTranslate or ExpressionOp.HexIndex or ExpressionOp.HexMultiply => 12L,
        ExpressionOp.Morton or ExpressionOp.MortonX or ExpressionOp.MortonY => 15L,

        // Tier 6: Multi-cycle integer division, modulo, and cyclic distance (16-18)
        ExpressionOp.Divide or ExpressionOp.Modulo => 16L,
        ExpressionOp.FloorModulo or ExpressionOp.CycleForward or ExpressionOp.CycleDistance => 18L,

        // Tier 7: Square roots, transcendentals, and quadratic sequences (20-25)
        ExpressionOp.SquareRoot => 20L,
        ExpressionOp.Layer or ExpressionOp.LayerOffset or ExpressionOp.LayerStart or ExpressionOp.LayerSize => 20L,
        ExpressionOp.Sine or ExpressionOp.Cosine => 25L,

        // Tier 8: Shell unpairing & square root locators (25-35)
        ExpressionOp.PairX or ExpressionOp.PairY or ExpressionOp.PairSwap
            or ExpressionOp.PairMax or ExpressionOp.PairMin or ExpressionOp.PairSum or ExpressionOp.PairDifference
            or ExpressionOp.SquareX or ExpressionOp.SquareY => 25L,
        ExpressionOp.HexRadius => 30L,
        ExpressionOp.HexQ or ExpressionOp.HexR => 35L,

        // Tier 9: Table lookups (3)
        ExpressionOp.Factorial or ExpressionOp.Prime => 3L,

        // Tier 10: Combinatorial & iterative Euclidean loops (40-80)
        ExpressionOp.GreatestCommonDivisor or ExpressionOp.Choose => 40L,
        ExpressionOp.LeastCommonMultiple => 45L,
        ExpressionOp.Hilbert or ExpressionOp.HilbertX or ExpressionOp.HilbertY => 60L,
        ExpressionOp.SubsetRank or ExpressionOp.SubsetAt or ExpressionOp.SubsetMember
            or ExpressionOp.ArrangementRank or ExpressionOp.ArrangementAt or ExpressionOp.ArrangementMember => 60L,

        // Tier 11: Heavy primality test (200)
        ExpressionOp.IsPrime => 200L,

        // Tier 12: Topology shifts & transforms
        ExpressionOp.BoardShift or ExpressionOp.BoardImage => (board is { } b ? (b.Topology.CellCount / 2 + b.Visits) : 24L),
        // A fill is at most one shift per cell before the frontier empties.
        ExpressionOp.BoardFill => (board is { } fill ? ((long)fill.Topology.CellCount * (fill.Topology.CellCount / 2 + fill.Visits)) : 1_536L),

        _ => 1L,
    };

    /// <summary>Returns the work units an expression costs: the sum of its operations and live operand reads.</summary>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        var cost = 0L;

        foreach (var token in tokens) {
            var tokenCost = ((token.Operand is { } operand)
                ? operand.Cost(context: context)
                : OperationCost(operation: token.Operation, board: token.Board));
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
