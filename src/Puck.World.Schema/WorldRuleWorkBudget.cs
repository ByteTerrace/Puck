namespace Puck.World;

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
        var slots = 0L;
        var work = 0L;
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if (rule.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(neighbors.CellWidth.Value); }
                }
            }
            var multiplier = ((rule.ForEach is { } rowName)
                ? (RowCapacity(definition, rowName))
                : 1
            );
            slots = SaturatingAdd(left: slots, right: multiplier);
            work = SaturatingAdd(left: work, right: SaturatingMultiply(left: multiplier, right: RuleCost(rule: rule, definition: definition)));
        }

        foreach (var interaction in interactions) {
            var multiplier = ((interaction.Interaction?.CoOccurrence == WorldInteractionCoOccurrence.Distance)
                ? ((long)definition.Population.Capacity * Math.Max(val1: 0, val2: (definition.Population.Capacity - 1)))
                : definition.Population.Capacity
            );
            slots = SaturatingAdd(left: slots, right: multiplier);
            work = SaturatingAdd(left: work, right: SaturatingMultiply(left: multiplier, right: RuleCost(rule: interaction, definition: definition)));
        }

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
