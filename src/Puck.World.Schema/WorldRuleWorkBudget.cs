namespace Puck.World;

/// <summary>One rule's or interaction's line in the per-tick work sheet.</summary>
/// <param name="Name">The rule or interaction name.</param>
/// <param name="IsInteraction">Whether the line is an interaction's.</param>
/// <param name="Multiplier">How many evaluations one tick can run: a <c>forEach</c> row's capacity, an interaction's
/// carrier or pair count, or 1.</param>
/// <param name="UnitCost">The cost of one evaluation.</param>
/// <param name="WorkUnits">The line's total, <see cref="Multiplier"/> times <see cref="UnitCost"/>.</param>
/// <param name="Discriminators">The literal cells the gate pins to ranges, in cell order; empty when the line sums
/// into the total unconditionally. Lines pinning the same cell to disjoint ranges price at their costliest ranges
/// rather than their sum, one range more per rule that can write the cell in a tick, and a line pinning a further
/// cell nests under the lines pinning fewer.</param>
public readonly record struct WorldRuleWorkContributor(string Name, bool IsInteraction, long Multiplier, long UnitCost, long WorkUnits, IReadOnlyList<WorldRulePinnedCell> Discriminators);

/// <summary>One literal cell a gate pins: the inclusive raw range the cell must lie in for the gate to hold, in the
/// row's own encoding (fixed-point rows carry raw bits).</summary>
/// <param name="Cell">The <c>row.key</c> cell.</param>
/// <param name="Low">The lowest admitted raw value; <see cref="long.MinValue"/> when unbounded below.</param>
/// <param name="High">The highest admitted raw value; <see cref="long.MaxValue"/> when unbounded above. Below
/// <see cref="Low"/> when the gate can never hold.</param>
public readonly record struct WorldRulePinnedCell(string Cell, long Low, long High) {
    /// <summary>Gets a value indicating whether the range is empty, so the gate can never hold.</summary>
    public bool IsEmpty => (High < Low);
    /// <summary>Gets a value indicating whether a raw value lies in the range.</summary>
    /// <param name="value">The raw value.</param>
    public bool Contains(long value) => ((Low <= value) && (value <= High));
    /// <summary>Gets a value indicating whether two ranges share no value.</summary>
    /// <param name="other">The other range.</param>
    public bool Disjoint(WorldRulePinnedCell other) => ((High < other.Low) || (other.High < Low));
    /// <summary>Formats the pin as <c>cell=v</c>, <c>cell&lt;=v</c>, <c>cell&gt;=v</c>, <c>cell=lo..hi</c>, or
    /// <c>cell=never</c>.</summary>
    public string Describe() {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (IsEmpty) { return $"{Cell}=never"; }
        if (Low == High) { return $"{Cell}={Low.ToString(provider: culture)}"; }
        if (Low == long.MinValue) { return $"{Cell}<={High.ToString(provider: culture)}"; }
        if (High == long.MaxValue) { return $"{Cell}>={Low.ToString(provider: culture)}"; }
        return $"{Cell}={Low.ToString(provider: culture)}..{High.ToString(provider: culture)}";
    }
}

/// <summary>The statically derived worst-case rule, interaction, and flock-affinity work admitted for one tick.</summary>
/// <param name="RuleRows">The authored ordinary-rule count.</param>
/// <param name="InteractionRows">The authored interaction count.</param>
/// <param name="EvaluationSlots">The greatest number of rule/interaction bindings evaluated in one tick.</param>
/// <param name="WorkUnitsPerTick">The conservative token/effect/candidate-visit total, including flock-affinity and decision image/grid point visits.
/// These structural units are not a CPU-time or sort-comparison bound.</param>
/// <param name="FlockAffinityWorkUnitsPerTick">The included worst-case affinity cost if every body refreshes its most expensive eligible producer.</param>
/// <param name="DecisionImagePointsPerTick">Maximum active poses copied before the ordinary rule pass, zero without neighbor options.</param>
/// <param name="DecisionGridBuildsPerTick">Maximum shared grid rebuilds, one per distinct power-of-two neighbor range scale.</param>
/// <param name="DecisionGridPointsPerTick">Maximum points sorted across those rebuilds. Each rebuild also visits its points twice, included in WorkUnitsPerTick.</param>
public readonly record struct WorldRuleWorkBudget(int RuleRows, int InteractionRows, long EvaluationSlots, long WorkUnitsPerTick,
    long FlockAffinityWorkUnitsPerTick = 0, int DecisionImagePointsPerTick = 0, int DecisionGridBuildsPerTick = 0, long DecisionGridPointsPerTick = 0) {
    /// <summary>Derives the conservative per-tick work sheet from a validated definition.</summary>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = WorldRuleCompiler.CompileAll(definition: definition);
        var interactions = WorldRuleCompiler.CompileAllInteractions(definition: definition);
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if (rule.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(neighbors.CellWidth.Value); }
                }
            }
        }

        var (slots, work) = Tally(
            contributors: Enumerate(definition: definition, rules: rules, interactions: interactions),
            writers: CountWriters(definition: definition, rules: rules, interactions: interactions)
        );

        var flockCost = FlockAffinityCost(definition);
        var imagePoints = decisionScales.Count == 0 ? 0 : definition.Population.Capacity;
        var gridPoints = (long)definition.Population.Capacity * decisionScales.Count;
        // All policies may reconsider together. A pose image is shared; each grid copies and groups its points.
        // Sorting those grid points has a separate, explicit read-back rather than pretending a visit is a comparison.
        var perceptionCost = SaturatingAdd(imagePoints, SaturatingMultiply(2, gridPoints));
        return new WorldRuleWorkBudget(
            RuleRows: rules.Length,
            InteractionRows: interactions.Length,
            EvaluationSlots: slots,
            WorkUnitsPerTick: SaturatingAdd(SaturatingAdd(work, flockCost), perceptionCost),
            FlockAffinityWorkUnitsPerTick: flockCost,
            DecisionImagePointsPerTick: imagePoints,
            DecisionGridBuildsPerTick: decisionScales.Count,
            DecisionGridPointsPerTick: gridPoints
        );
    }

    /// <summary>Lists every rule's and interaction's line in the work sheet, costliest first — the breakdown behind
    /// <see cref="WorkUnitsPerTick"/>'s one total.</summary>
    /// <param name="definition">The validated world.</param>
    /// <returns>The lines, by descending <see cref="WorldRuleWorkContributor.WorkUnits"/> then name.</returns>
    public static IReadOnlyList<WorldRuleWorkContributor> Contributors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var contributors = Enumerate(
            definition: definition,
            rules: WorldRuleCompiler.CompileAll(definition: definition),
            interactions: WorldRuleCompiler.CompileAllInteractions(definition: definition)
        );

        contributors.Sort(comparison: static (left, right) => {
            var byWork = right.WorkUnits.CompareTo(value: left.WorkUnits);

            return ((byWork != 0) ? byWork : string.CompareOrdinal(strA: left.Name, strB: right.Name));
        });

        return contributors;
    }

    private static List<WorldRuleWorkContributor> Enumerate(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var contributors = new List<WorldRuleWorkContributor>(capacity: rules.Length + interactions.Length);

        foreach (var rule in rules) {
            var multiplier = ((rule.ForEach is { } rowName)
                ? (RowCapacity(definition, rowName))
                : 1
            );
            var unit = RuleCost(rule: rule, definition: definition);

            contributors.Add(item: new WorldRuleWorkContributor(
                Name: rule.Name,
                IsInteraction: false,
                Multiplier: multiplier,
                UnitCost: unit,
                WorkUnits: SaturatingMultiply(left: multiplier, right: unit),
                Discriminators: PinnedCells(gate: rule.Gate, contradictory: out _)
            ));
        }

        foreach (var interaction in interactions) {
            var multiplier = ((interaction.Interaction?.CoOccurrence == WorldInteractionCoOccurrence.Distance)
                ? ((long)definition.Population.Capacity * Math.Max(val1: 0, val2: (definition.Population.Capacity - 1)))
                : definition.Population.Capacity
            );
            var unit = RuleCost(rule: interaction, definition: definition);

            contributors.Add(item: new WorldRuleWorkContributor(
                Name: interaction.Name,
                IsInteraction: true,
                Multiplier: multiplier,
                UnitCost: unit,
                WorkUnits: SaturatingMultiply(left: multiplier, right: unit),
                Discriminators: []
            ));
        }

        return contributors;
    }

    /// <summary>Lists the rules whose gates can never hold: a literal cell pinned to an empty range.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>Each such rule and the cell it pins empty.</returns>
    public static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        var result = new List<(string, string)>();

        foreach (var rule in WorldRuleCompiler.CompileAll(definition: definition)) {
            foreach (var pinned in PinnedCells(gate: rule.Gate, contradictory: out _)) {
                if (pinned.IsEmpty) {
                    result.Add(item: (rule.Name, pinned.Cell));
                }
            }
        }

        return result;
    }

    /// <summary>Reads the literal cells a gate pins to ranges: a single comparison against a constant, or the
    /// comparison conjuncts of a top-level <c>all</c>, intersected per cell. Other conjuncts narrow the gate further
    /// and pin nothing here; <c>notEqual</c> pins nothing.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="contradictory">Whether some cell's range came out empty, so the gate can never hold.</param>
    /// <returns>The pins in cell order.</returns>
    public static IReadOnlyList<WorldRulePinnedCell> PinnedCells(CompiledWorldPredicate[] gate, out bool contradictory) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        contradictory = false;
        if (gate.Length == 0) {
            return [];
        }
        var last = gate[^1];
        var conjuncts = 0;
        if (last.Kind == CompiledWorldPredicateKind.Compare) {
            conjuncts = 1;
        } else if (last.Kind == CompiledWorldPredicateKind.All && last.Arity == gate.Length - 1) {
            conjuncts = last.Arity;
        } else {
            return [];
        }
        var pinned = new SortedDictionary<string, (long Low, long High)>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < conjuncts; index++) {
            var predicate = gate[index];
            if (predicate.Kind != CompiledWorldPredicateKind.Compare) {
                return [];
            }
            if (predicate.Comparand is not null || predicate.LeftExpression is not null ||
                predicate.Left is not { Value: StateCellOperand { Key: { } key, KeyFrom: null } state }) {
                continue;
            }
            var value = predicate.Value;
            (long Low, long High) range = predicate.Comparison switch {
                Puck.Physics.Motion.ActionStateComparison.Equal => (value, value),
                Puck.Physics.Motion.ActionStateComparison.Less => (long.MinValue, (value == long.MinValue) ? long.MinValue : value - 1),
                Puck.Physics.Motion.ActionStateComparison.LessOrEqual => (long.MinValue, value),
                Puck.Physics.Motion.ActionStateComparison.Greater => ((value == long.MaxValue) ? long.MaxValue : value + 1, long.MaxValue),
                Puck.Physics.Motion.ActionStateComparison.GreaterOrEqual => (value, long.MaxValue),
                _ => (long.MinValue, long.MaxValue),
            };
            if (predicate.Comparison == Puck.Physics.Motion.ActionStateComparison.Less && value == long.MinValue) { range = (1L, 0L); }
            if (predicate.Comparison == Puck.Physics.Motion.ActionStateComparison.Greater && value == long.MaxValue) { range = (1L, 0L); }
            if (range == (long.MinValue, long.MaxValue)) {
                continue;
            }
            var cell = $"{state.Row}.{key}";
            pinned[cell] = (pinned.TryGetValue(cell, out var existing)
                ? (Math.Max(existing.Low, range.Low), Math.Min(existing.High, range.High))
                : range);
        }
        var result = new WorldRulePinnedCell[pinned.Count];
        var position = 0;
        foreach (var (cell, (low, high)) in pinned) {
            result[position++] = new WorldRulePinnedCell(Cell: cell, Low: low, High: high);
            contradictory |= (high < low);
        }
        return result;
    }

    // Rules whose gates pin the same literal cells to disjoint ranges cannot all fire on one tick: the sheet
    // charges a set of such rules its costliest ranges rather than their sum. Each line sits at the node of the
    // trie its pinned cells spell (in cell order, so "phase=1, sub=0" and "sub=0, phase=1" share a node); a node's
    // worst case is its own lines plus, per further cell its children pin, the costliest points of that cell —
    // the lines whose ranges contain one value summed, taken for one value plus one more per rule evaluation that
    // can write the cell during the tick, since effects apply immediately and a rule advancing a phase lets the
    // next phase's rules fire in the same tick. Children pinning different cells are not exclusive of each other
    // and sum.
    private static (long Slots, long Work) Tally(List<WorldRuleWorkContributor> contributors, Dictionary<string, long> writers) {
        var slots = 0L;
        var root = new ExclusionNode();

        foreach (var contributor in contributors) {
            slots = SaturatingAdd(left: slots, right: contributor.Multiplier);
            var node = root;

            foreach (var pinned in contributor.Discriminators) {
                if (!node.Children.TryGetValue(pinned.Cell, out var ranges)) {
                    ranges = [];
                    node.Children[pinned.Cell] = ranges;
                }
                var child = default(ExclusionNode);
                foreach (var (range, existing) in ranges) {
                    if (range.Low == pinned.Low && range.High == pinned.High) {
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
    private sealed class ExclusionNode {
        public long Own { get; set; }
        public Dictionary<string, List<(WorldRulePinnedCell Range, ExclusionNode Child)>> Children { get; } = new(comparer: StringComparer.Ordinal);
    }
    private static long Worst(ExclusionNode node, Dictionary<string, long> writers) {
        var total = node.Own;

        foreach (var (cell, ranges) in node.Children) {
            var worsts = new long[ranges.Count];
            for (var index = 0; index < ranges.Count; index++) { worsts[index] = Worst(node: ranges[index].Child, writers: writers); }

            // Every range's low end is a candidate value of the cell; the lines whose ranges contain it can fire
            // together. An empty range contains nothing and its lines never fire.
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
            var admitted = (int)Math.Min(val1: sums.Count, val2: SaturatingAdd(left: 1L, right: writers.GetValueOrDefault(cell)));

            for (var index = 0; index < admitted; index++) { total = SaturatingAdd(left: total, right: sums[index]); }
        }

        return total;
    }
    // How many times per tick each "row.key" cell can be written by a rule, counted per evaluation (a forEach rule
    // writing a slot once per key writes it capacity times). A write addressing a row without a literal key counts
    // against every cell of that row.
    private static Dictionary<string, long> CountWriters(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var byCell = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var byRow = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var rule in rules) {
            var multiplier = ((rule.ForEach is { } rowName) ? RowCapacity(definition, rowName) : 1L);
            CountRuleWriters(rule: rule, multiplier: multiplier, byCell: byCell, byRow: byRow);
        }
        foreach (var interaction in interactions) {
            var multiplier = ((interaction.Interaction?.CoOccurrence == WorldInteractionCoOccurrence.Distance)
                ? ((long)definition.Population.Capacity * Math.Max(val1: 0, val2: (definition.Population.Capacity - 1)))
                : definition.Population.Capacity
            );
            CountRuleWriters(rule: interaction, multiplier: multiplier, byCell: byCell, byRow: byRow);
        }

        var writers = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        foreach (var (cell, count) in byCell) {
            writers[cell] = SaturatingAdd(left: count, right: byRow.GetValueOrDefault(cell[..cell.LastIndexOf(value: '.')]));
        }
        foreach (var rule in rules) {
            foreach (var pinned in PinnedCells(gate: rule.Gate, contradictory: out _)) {
                if (!writers.ContainsKey(key: pinned.Cell)) {
                    writers[pinned.Cell] = byRow.GetValueOrDefault(pinned.Cell[..pinned.Cell.LastIndexOf(value: '.')]);
                }
            }
        }
        return writers;
    }
    private static void CountRuleWriters(CompiledWorldRule rule, long multiplier, Dictionary<string, long> byCell, Dictionary<string, long> byRow) {
        foreach (var write in WorldRuleDataflow.Writes(rule: rule)) {
            var into = ((write.Key is null) ? byRow : byCell);
            var key = ((write.Key is null) ? write.Row : $"{write.Row}.{write.Key}");
            into[key] = SaturatingAdd(left: into.GetValueOrDefault(key), right: multiplier);
        }
    }
    private static long RuleCost(CompiledWorldRule rule, WorldDefinition definition) {
        var cost = SaturatingAdd(1, PredicateCost(rule.Gate, definition));
        foreach (var binding in (rule.Bindings ?? [])) {
            cost = SaturatingAdd(left: cost, right: ExpressionCost(binding.Expression, definition));
        }
        foreach (var effect in rule.Effects) {
            cost = SaturatingAdd(left: cost, right: EffectCost(effect: effect, definition: definition));
        }

        if (rule.Decision is { } decision) {
            var currentGate = 0L;
            var branch = EffectsCost(decision.OnNoChoice, definition);
            foreach (var option in decision.Options) {
                var gate = PredicateCost(option.Gate, definition);
                currentGate = Math.Max(currentGate, gate);
                var scoreCost = SaturatingAdd(SaturatingAdd(1, gate), ExpressionCost(option.Score, definition));
                if (option.Neighbors is { } neighbors) {
                    // Physical sampling and eligibility inspect at most the candidate budget; only retained candidates score.
                    cost = SaturatingAdd(cost, SaturatingMultiply(neighbors.Source.CandidateBudget, SaturatingAdd(1, gate)));
                    cost = SaturatingAdd(cost, SaturatingMultiply(neighbors.Source.MaxCandidates, SaturatingAdd(1, ExpressionCost(option.Score, definition))));
                    cost = SaturatingAdd(cost, 27); // Grid cell lookups, independent of crowd density.
                    if (neighbors.Source.RequiresLineOfSight) {
                        cost = SaturatingAdd(cost, SaturatingMultiply(neighbors.Source.CandidateBudget, 1));
                    }
                } else { cost = SaturatingAdd(cost, scoreCost); }
                branch = Math.Max(branch, EffectsCost(option.Effects, definition));
            }
            cost = SaturatingAdd(cost, currentGate);
            cost = SaturatingAdd(cost, PredicateCost(decision.Interrupt ?? [], definition));
            cost = SaturatingAdd(cost, branch);
        }

        return cost;
    }

    private static long PredicateCost(CompiledWorldPredicate[] tokens, WorldDefinition definition) {
        var cost = 0L;
        foreach (var token in tokens) {
            if (token.LeftExpression is { } expression) {
                cost = SaturatingAdd(cost, SaturatingAdd(1, SaturatingAdd(
                    ExpressionCost(expression, definition), ExpressionCost(token.RightExpression!, definition))));
                continue;
            }
            if (token.Left is { } left) { cost = SaturatingAdd(cost, OperandCost(left, definition)); }
            if (token.Comparand is { } comparand) { cost = SaturatingAdd(cost, OperandCost(comparand, definition)); }
        }
        return cost;
    }

    /// <summary>Counts tokens and conservative state-candidate visits for a validated expression.</summary>
    /// <param name="tokens">The compiled postfix expression.</param>
    /// <param name="definition">The world whose state capacities bound indirect reads and reductions.</param>
    /// <returns>The conservative work units for one evaluation.</returns>
    /// <exception cref="ArgumentNullException">The tokens or definition is null.</exception>
    public static long ExpressionCost(CompiledWorldExpressionToken[] tokens, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(definition);
        var cost = 0L;
        foreach (var token in tokens) {
            cost = SaturatingAdd(cost, token.Operand is { } operand ? OperandCost(operand, definition) : 1);
        }
        return cost;
    }

    private static long FlockAffinityCost(WorldDefinition definition) {
        var maximum = 0L;
        foreach (var kit in definition.Kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } profile) { continue; }
                var compiled = new CompiledWorldFlockAffinities(profile, definition);
                maximum = Math.Max(maximum, SaturatingMultiply(profile.MaxNeighbors, compiled.WorkUnitsPerNeighbor));
            }
        }
        // Initial samples and explicit producer changes may align every observer, regardless of normal cadence.
        return SaturatingMultiply(definition.Population.Capacity, maximum);
    }

    private static long EffectCost(CompiledWorldEffect effect, WorldDefinition definition) {
        var cost = effect.Kind switch {
            WorldRuleEffectKind.Write or WorldRuleEffectKind.Countdown or WorldRuleEffectKind.RemoveStateCell or WorldRuleEffectKind.ScheduleState => 512L,
            WorldRuleEffectKind.PushState => 1_024L,
            WorldRuleEffectKind.Generate => 4_096L,
            WorldRuleEffectKind.TransformState => TransformCost(((TransformStateEffect)effect.Value!).Transform, definition),
            WorldRuleEffectKind.UpsertHudPanel or WorldRuleEffectKind.RemoveHudPanel => 4_096L,
            WorldRuleEffectKind.UpsertPlacement or WorldRuleEffectKind.RemovePlacement => 32_768L,
            _ => 1L,
        };

        if (effect.Value is IValueSourcedEffect { From: { } source }) {
            cost = SaturatingAdd(left: cost, right: OperandCost(operand: source, definition: definition));
        }
        if (effect.Value is IValueSourcedEffect { Expression: { } expression }) {
            foreach (var token in expression) {
                cost = SaturatingAdd(
                    left: cost,
                    right: ((token.Operand is { } operand)
                        ? OperandCost(operand: operand, definition: definition)
                        : 1L)
                );
            }
        }
        if (effect.Value is PaintFieldEffect { Paint: var paint }) {
            var diameter = ((2L * paint.Radius) + 1L);
            cost = SaturatingAdd(left: cost, right: SaturatingMultiply(left: diameter, right: SaturatingMultiply(left: diameter, right: diameter)));
        }
        if (effect.Value is TransactionEffect { Effects: { } main } transaction) {
            var mainCost = EffectsCost(effects: main, definition: definition);
            var failureCost = EffectsCost(effects: transaction.OnFailure, definition: definition);
            // Success preflights and applies main. Refusal may inspect all of main, then preflight and apply failure.
            var success = SaturatingMultiply(left: 2L, right: mainCost);
            var refusal = SaturatingAdd(
                left: mainCost,
                right: SaturatingMultiply(left: 2L, right: failureCost)
            );
            cost = SaturatingAdd(left: cost, right: Math.Max(val1: success, val2: refusal));
        }

        return cost;
    }

    private static long TransformCost(WorldStateTransform transform, WorldDefinition definition) {
        var storage = definition.State.Sum(static row => (long)row.CellCeiling);
        var cost = 4096L + storage;
        switch (transform) {
            case WorldStateTransform.SetRay ray:
                var board = WorldDefinitionRows.FindStateRow(definition.State, ray.Row)!;
                var count = WorldTopologyCompilation.Find(definition.StateRaw, ((WorldStateDomain.CellsOf)board.EffectiveDomain).Topology)!.CellCount;
                cost += (long)count * (count + 2);
                break;
            case WorldStateTransform.Transfer transfer:
                var dealt = WorldDefinitionRows.FindStateRow(definition.State, transfer.From)!;
                cost += (long)transfer.Count * (dealt.Capacity ?? dealt.CellCeiling);
                break;
            case WorldStateTransform.SortZone sortZone:
                var sortedZone = WorldDefinitionRows.FindStateRow(definition.State, sortZone.Row)!;
                cost += 2L * (sortedZone.Capacity ?? sortedZone.CellCeiling) * Math.Max(1, sortZone.By.Count);
                break;
            case WorldStateTransform.SortKeyed sortKeyed:
                var sortedKeyed = WorldDefinitionRows.FindStateRow(definition.State, sortKeyed.Row)!;
                cost += 2L * (sortedKeyed.Capacity ?? sortedKeyed.CellCeiling);
                break;
            case WorldStateTransform.Shuffle shuffle:
                var pile = WorldDefinitionRows.FindStateRow(definition.State, shuffle.Row)!;
                cost += 2L * (pile.Capacity ?? pile.CellCeiling);
                break;
            case WorldStateTransform.WriteSet writeSet:
                var written = WorldDefinitionRows.FindStateRow(definition.State, writeSet.Row)!;
                var writtenCells = WorldTopologyCompilation.Find(definition.StateRaw, ((WorldStateDomain.CellsOf)written.EffectiveDomain).Topology)!.CellCount;
                cost += (long)writtenCells * (writtenCells + 1);
                break;
            case WorldStateTransform.Push push:
                var ring = WorldDefinitionRows.FindStateRow(definition.State, push.Row)!;
                cost += 2L * ((ring.EffectiveDomain as WorldStateDomain.Ring)?.Capacity ?? 1);
                break;
            case WorldStateTransform.Observe observe:
                var row = WorldDefinitionRows.FindStateRow(definition.State, observe.Row)!;
                var cells = WorldTopologyCompilation.Find(definition.StateRaw, ((WorldStateDomain.CellsOf)row.EffectiveDomain).Topology)!.CellCount;
                cost += (long)cells * (cells + 3);
                break;
        }
        return cost;
    }
    private static long EffectsCost(CompiledWorldEffect[] effects, WorldDefinition definition) {
        var cost = 0L;
        foreach (var effect in effects) {
            cost = SaturatingAdd(left: cost, right: EffectCost(effect: effect, definition: definition));
        }
        return cost;
    }

    private static long OperandCost(CompiledWorldOperand operand, WorldDefinition definition) {
        // A board query prices as itself regardless of which case carries it — a Board operand always, a Pattern
        // operand only when its word source is a board ray (see PatternOperand.Board's own remarks).
        var board = operand.Value switch {
            BoardOperand b => b.Board,
            PatternOperand { Board: { } patternBoard } => patternBoard,
            _ => null,
        };
        if (board is { } query) {
            var visits = query switch {
                BoardPathCostQuery pathCost => (long)(pathCost.MaxVisits + 1) * (query.Topology.CellCount + query.Topology.DirectionCount),
                BoardCanonicalQuery => (long)query.Topology.CellCount * query.Topology.ElementCount,
                BoardAttacksQuery attacks => (long)query.Topology.CellCount * attacks.Directions.Length,
                _ => query.Topology.CellCount,
            };
            return query.Topology.CellCount + visits;
        }
        if (operand.Value is ReductionOperand or ArgBodyOperand) {
            var row = operand.Value switch {
                ReductionOperand reduction => reduction.Row,
                ArgBodyOperand argBody => argBody.Row,
                _ => string.Empty,
            };
            return RowCapacity(definition, row);
        }
        // PhysicsQuiescent scans every population slot for an active rigid body not yet at rest
        // (WorldPopulation.RigidBodiesQuiescent) — the same per-tick cost as Nearest/RegionOccupancy's own
        // capacity-wide scan, so it is priced on the same terms rather than read as a free operand.
        if (operand.Value is NearestOperand or RegionOccupancyOperand or PhysicsQuiescentOperand) {
            return definition.Population.Capacity;
        }
        if (operand.Value is TableOperand table) {
            // A binary search over the sorted keys, plus the key read.
            return 2L + System.Numerics.BitOperations.Log2((uint)Math.Max(table.EntryCount, 1));
        }
        if (operand.Value is PatternOperand pattern) {
            // Reached only when pattern.Board is null (a board-sourced pattern already returned above).
            return WorldPatternCapacity.MaxWord * (1L + (pattern.TokenExpression?.Length ?? 0));
        }

        return 1L;
    }

    private static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right)) ? long.MaxValue : (left + right));
    private static long SaturatingMultiply(long left, long right) => ((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right)) ? long.MaxValue : (left * right));
    private static int RowCapacity(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, name);
        return row?.Capacity ?? row?.CellCeiling ?? WorldStateCapacity.MaxCellsPerRow;
    }
}
