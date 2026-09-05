namespace Puck.World;

/// <summary>One rule's or interaction's line in the per-tick work sheet.</summary>
/// <param name="Name">The rule or interaction name.</param>
/// <param name="IsInteraction">Whether the line is an interaction's.</param>
/// <param name="Multiplier">How many evaluations one tick can run: a <c>forEach</c> row's capacity, an interaction's
/// carrier or pair count, or 1.</param>
/// <param name="UnitCost">The cost of one evaluation.</param>
/// <param name="WorkUnits">The line's total, <see cref="Multiplier"/> times <see cref="UnitCost"/>.</param>
/// <param name="Discriminators">The literal cells the gate pins to constants, as <c>row.key=value</c> in cell order;
/// empty when the line sums into the total unconditionally. Lines pinning the same cell to different values price
/// at their costliest values rather than their sum, one value more per rule that can write the cell in a tick, and
/// a line pinning a further cell nests under the lines pinning fewer.</param>
public readonly record struct WorldRuleWorkContributor(string Name, bool IsInteraction, long Multiplier, long UnitCost, long WorkUnits, IReadOnlyList<string> Discriminators);

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
                Discriminators: Discriminators(gate: rule.Gate)
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

    // Rules whose gates pin the same literal cells to distinct constants cannot all fire on one tick: the sheet
    // charges a set of such rules its costliest values rather than their sum. Each line sits at the node of the
    // trie its pinned cells spell (in cell order, so "phase=1, sub=0" and "sub=0, phase=1" share a node); a node's
    // worst case is its own lines plus, per further cell its children pin, the costliest child values — one value,
    // plus one more per rule that can write that cell during the tick, since effects apply immediately and a rule
    // advancing a phase lets the next phase's rules fire in the same tick. Children pinning different cells are
    // not exclusive of each other and sum.
    private static (long Slots, long Work) Tally(List<WorldRuleWorkContributor> contributors, Dictionary<string, long> writers) {
        var slots = 0L;
        var root = new ExclusionNode();

        foreach (var contributor in contributors) {
            slots = SaturatingAdd(left: slots, right: contributor.Multiplier);
            var node = root;

            foreach (var discriminator in contributor.Discriminators) {
                var separator = discriminator.LastIndexOf(value: '=');
                var cell = discriminator[..separator];
                var value = long.Parse(s: discriminator.AsSpan(start: separator + 1), provider: System.Globalization.CultureInfo.InvariantCulture);

                if (!node.Children.TryGetValue(cell, out var byValue)) {
                    byValue = [];
                    node.Children[cell] = byValue;
                }
                if (!byValue.TryGetValue(value, out var child)) {
                    child = new ExclusionNode();
                    byValue[value] = child;
                }
                node = child;
            }

            node.Own = SaturatingAdd(left: node.Own, right: contributor.WorkUnits);
        }

        return (slots, Worst(node: root, writers: writers));
    }
    private sealed class ExclusionNode {
        public long Own { get; set; }
        public Dictionary<string, Dictionary<long, ExclusionNode>> Children { get; } = new(comparer: StringComparer.Ordinal);
    }
    private static long Worst(ExclusionNode node, Dictionary<string, long> writers) {
        var total = node.Own;

        foreach (var (cell, byValue) in node.Children) {
            var worsts = new List<long>(capacity: byValue.Count);

            foreach (var child in byValue.Values) { worsts.Add(item: Worst(node: child, writers: writers)); }

            worsts.Sort(comparison: static (left, right) => right.CompareTo(value: left));
            var admitted = (int)Math.Min(val1: worsts.Count, val2: SaturatingAdd(left: 1L, right: writers.GetValueOrDefault(cell)));

            for (var index = 0; index < admitted; index++) { total = SaturatingAdd(left: total, right: worsts[index]); }
        }

        return total;
    }
    // Every literal cell the gate pins Equal to a constant, as "row.key=value" in cell order: a single Compare, or the
    // Compare conjuncts of a top-level All (other conjuncts narrow the gate further and cost nothing here). A cell
    // pinned twice keeps its first value.
    private static string[] Discriminators(CompiledWorldPredicate[] gate) {
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
        var pinned = new SortedDictionary<string, long>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < conjuncts; index++) {
            var predicate = gate[index];
            if (predicate.Kind != CompiledWorldPredicateKind.Compare) {
                return [];
            }
            if (predicate.Comparison == Puck.Physics.Motion.ActionStateComparison.Equal && predicate.Comparand is null && predicate.LeftExpression is null &&
                predicate.Left is { Value: StateCellOperand { Key: { } key, KeyFrom: null } state }) {
                pinned.TryAdd(key: $"{state.Row}.{key}", value: predicate.Value);
            }
        }
        var result = new string[pinned.Count];
        var position = 0;
        foreach (var (cell, value) in pinned) {
            result[position++] = $"{cell}={value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return result;
    }
    // How many times per tick each "row.key" cell can be written by a rule, counted per evaluation (a forEach rule
    // writing a slot once per key writes it capacity times). An effect addressing a row without a literal key — a
    // "$cell:" indirection, a push, a generate, a transform — counts against every cell of that row.
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
            var row = cell[..cell.LastIndexOf(value: '.')];
            writers[cell] = SaturatingAdd(left: count, right: byRow.GetValueOrDefault(row));
        }
        foreach (var (row, count) in byRow) {
            foreach (var rule in rules) {
                foreach (var discriminator in Discriminators(gate: rule.Gate)) {
                    var cell = discriminator[..discriminator.LastIndexOf(value: '=')];
                    if (!writers.ContainsKey(key: cell) && string.Equals(a: cell[..cell.LastIndexOf(value: '.')], b: row, comparisonType: StringComparison.Ordinal)) {
                        writers[cell] = count;
                    }
                }
            }
        }
        return writers;
    }
    private static void CountRuleWriters(CompiledWorldRule rule, long multiplier, Dictionary<string, long> byCell, Dictionary<string, long> byRow) {
        CountEffectWriters(effects: rule.Effects, multiplier: multiplier, byCell: byCell, byRow: byRow);
        if (rule.Decision is { } decision) {
            CountEffectWriters(effects: decision.OnNoChoice, multiplier: multiplier, byCell: byCell, byRow: byRow);
            foreach (var option in decision.Options) {
                CountEffectWriters(effects: option.Effects, multiplier: multiplier, byCell: byCell, byRow: byRow);
            }
        }
    }
    private static void CountEffectWriters(CompiledWorldEffect[] effects, long multiplier, Dictionary<string, long> byCell, Dictionary<string, long> byRow) {
        foreach (var effect in effects) {
            switch (effect.Value) {
                case TransactionEffect transaction:
                    CountEffectWriters(effects: transaction.Effects, multiplier: multiplier, byCell: byCell, byRow: byRow);
                    CountEffectWriters(effects: transaction.OnFailure, multiplier: multiplier, byCell: byCell, byRow: byRow);
                    break;
                case IStateWriteEffect write:
                    Count(write.KeyFrom is null ? byCell : byRow, write.KeyFrom is null ? $"{write.Row}.{write.Key}" : write.Row, multiplier);
                    break;
                case RemoveStateCellEffect remove:
                    Count(remove.KeyFrom is null ? byCell : byRow, remove.KeyFrom is null ? $"{remove.Row}.{remove.Key}" : remove.Row, multiplier);
                    break;
                case GenerateEffect generate:
                    Count(byRow, generate.Row, multiplier);
                    break;
                case PushStateEffect push:
                    Count(byRow, push.Row, multiplier);
                    break;
                case TransformStateEffect transform:
                    foreach (var row in TransformRows(transform.Transform)) { Count(byRow, row, multiplier); }
                    break;
            }
        }

        static void Count(Dictionary<string, long> into, string key, long multiplier) =>
            into[key] = SaturatingAdd(left: into.GetValueOrDefault(key), right: multiplier);
    }
    private static IEnumerable<string> TransformRows(WorldStateTransform transform) => transform switch {
        WorldStateTransform.Transfer transfer => [transfer.From, transfer.To],
        WorldStateTransform.SetRay ray => [ray.Row],
        WorldStateTransform.Shuffle shuffle => [shuffle.Row],
        WorldStateTransform.SortZone zone => [zone.Row],
        WorldStateTransform.SortKeyed keyed => [keyed.Row],
        WorldStateTransform.WriteSet set => [set.Row],
        WorldStateTransform.Push push => [push.Row],
        WorldStateTransform.Observe observe => [observe.Row],
        _ => [],
    };
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
