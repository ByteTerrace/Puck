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
            cost = SaturatingAdd(cost, OperandCost(token.Left, definition));
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
            WorldRuleEffectKind.Generate => 4_096L,
            WorldRuleEffectKind.TransformState => TransformCost(effect.Transform!, definition),
            WorldRuleEffectKind.UpsertHudPanel or WorldRuleEffectKind.RemoveHudPanel => 4_096L,
            WorldRuleEffectKind.UpsertPlacement or WorldRuleEffectKind.RemovePlacement => 32_768L,
            _ => 1L,
        };

        if (effect.From is { } source) {
            cost = SaturatingAdd(left: cost, right: OperandCost(operand: source, definition: definition));
        }
        if (effect.Expression is { } expression) {
            foreach (var token in expression) {
                cost = SaturatingAdd(
                    left: cost,
                    right: ((token.Operand is { } operand)
                        ? OperandCost(operand: operand, definition: definition)
                        : 1L)
                );
            }
        }
        if (effect.Paint is { } paint) {
            var diameter = ((2L * paint.Radius) + 1L);
            cost = SaturatingAdd(left: cost, right: SaturatingMultiply(left: diameter, right: SaturatingMultiply(left: diameter, right: diameter)));
        }
        if (effect.SocialRelationship is { } relationship) {
            cost = SaturatingAdd(cost, SocialRelationshipCost(relationship, definition));
        }
        if (effect.SocialObservation is { } social) {
            cost = SaturatingAdd(cost, SocialRelationshipCost(social.Relationship, definition));
            cost = SaturatingAdd(cost, SocialEntityCost(social.Origin, definition));
            if (social.Source is { } sourceEntity) { cost = SaturatingAdd(cost, SocialEntityCost(sourceEntity, definition)); }
            cost = SaturatingAdd(cost, ExpressionCost(social.Sequence, definition));
            cost = SaturatingAdd(cost, ExpressionCost(social.OccurredAt, definition));
            cost = SaturatingAdd(cost, ExpressionCost(social.Value, definition));
            cost = SaturatingAdd(cost, ExpressionCost(social.Quality, definition));
        }
        if (effect.Effects is { } main) {
            var mainCost = EffectsCost(effects: main, definition: definition);
            var failureCost = EffectsCost(effects: (effect.OnFailure ?? []), definition: definition);
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
            case WorldStateTransform.MoveToken move:
                var terrain = WorldDefinitionRows.FindStateRow(definition.State, move.Terrain)!;
                var map = WorldTopologyCompilation.Find(definition.StateRaw, terrain.Board!.Topology)!;
                cost += (long)(move.MaxVisits + 1) * (map.CellCount + map.DirectionCount) + storage;
                break;
            case WorldStateTransform.SetRay ray:
                var board = WorldDefinitionRows.FindStateRow(definition.State, ray.Row)!;
                var count = WorldTopologyCompilation.Find(definition.StateRaw, board.Board!.Topology)!.CellCount;
                cost += (long)count * (count + 2);
                break;
            case WorldStateTransform.Shuffle shuffle:
                var pile = WorldDefinitionRows.FindStateRow(definition.State, shuffle.Row)!;
                cost += 2L * (pile.Capacity ?? pile.CellCeiling);
                break;
            case WorldStateTransform.Observe observe:
                var row = WorldDefinitionRows.FindStateRow(definition.State, observe.Row)!;
                var cells = WorldTopologyCompilation.Find(definition.StateRaw, row.Board!.Topology)!.CellCount;
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
        if (operand.Social is { } social) { return SocialRelationshipCost(social.Relationship, definition); }
        if (operand.Board is { } board) {
            var visits = board.Kind switch {
                WorldBoardQueryKind.Line => (long)board.Topology.CellCount * board.Topology.DirectionCount * (board.Length + 2),
                WorldBoardQueryKind.PathCost => (long)(board.MaxVisits + 1) * (board.Topology.CellCount + board.Topology.DirectionCount),
                _ => board.Topology.CellCount,
            };
            return board.Topology.CellCount + visits;
        }
        if (operand.Kind is WorldRuleFactKind.Reduction or WorldRuleFactKind.ArgBody) {
            return (RowCapacity(definition, operand.Row ?? string.Empty)
            );
        }
        if (operand.Kind is WorldRuleFactKind.Nearest or WorldRuleFactKind.RegionOccupancy) {
            return definition.Population.Capacity;
        }

        return 1L;
    }

    private static long SocialRelationshipCost(CompiledWorldSocialRelationship relationship, WorldDefinition definition) =>
        SaturatingAdd(1, SaturatingAdd(SocialEntityCost(relationship.Observer, definition), SocialEntityCost(relationship.Subject, definition)));

    private static long SocialEntityCost(CompiledWorldSocialEntity entity, WorldDefinition definition) =>
        entity.Body is { Kind: CompiledBodyRefKind.ArgMax or CompiledBodyRefKind.ArgMin, Row: { } row }
            ? RowCapacity(definition, row) : 1;

    private static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right)) ? long.MaxValue : (left + right));
    private static long SaturatingMultiply(long left, long right) => ((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right)) ? long.MaxValue : (left * right));
    private static int RowCapacity(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, name);
        return row?.Capacity ?? row?.CellCeiling ?? WorldStateCapacity.MaxCellsPerRow;
    }
}
