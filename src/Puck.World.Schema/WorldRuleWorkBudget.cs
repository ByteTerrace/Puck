namespace Puck.World;

/// <summary>The world's work sheet over <see cref="RuleWorkBudget"/>: rules multiplied by their <c>forEach</c> row's
/// capacity, interactions by the pair count their co-occurrence can visit, plus the flock-affinity and
/// decision-perception passes that run outside the rule sweep.</summary>
/// <param name="RuleRows">The rule count.</param>
/// <param name="InteractionRows">The interaction count.</param>
/// <param name="EvaluationSlots">The evaluations per tick, every multiplier summed.</param>
/// <param name="WorkUnitsPerTick">The worst-case work units per tick.</param>
/// <param name="FlockAffinityWorkUnitsPerTick">The flock-affinity share.</param>
/// <param name="DecisionImagePointsPerTick">The pose-image points a decision perception pass copies.</param>
/// <param name="DecisionGridBuildsPerTick">The grids a decision perception pass builds, one per distinct cell width.</param>
/// <param name="DecisionGridPointsPerTick">The points those grids group.</param>
public readonly record struct WorldRuleWorkBudget(int RuleRows, int InteractionRows, long EvaluationSlots, long WorkUnitsPerTick,
    long FlockAffinityWorkUnitsPerTick = 0, int DecisionImagePointsPerTick = 0, int DecisionGridBuildsPerTick = 0, long DecisionGridPointsPerTick = 0) {
    /// <summary>Measures a document's worst-case per-tick rule work.</summary>
    /// <param name="definition">The world.</param>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Measure(definition, WorldRuleCompiler.CompileAll(definition), WorldRuleCompiler.CompileAllInteractions(definition));
    }

    internal static WorldRuleWorkBudget Measure(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var context = WorldRuleCompiler.Context(definition: definition);
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if (rule.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(item: neighbors.CellWidth.Value); }
                }
            }
        }

        var lines = Lines(definition: definition, context: context, rules: rules, interactions: interactions);
        var (slots, work) = RuleWorkBudget.Tally(contributors: lines, writers: RuleWorkBudget.CountWriters(rules: Multiplied(definition: definition, context: context, rules: rules, interactions: interactions)));
        var flockCost = FlockAffinityCost(definition: definition, context: context);
        var imagePoints = ((decisionScales.Count == 0) ? 0 : definition.Population.Capacity);
        var gridPoints = ((long)definition.Population.Capacity * decisionScales.Count);
        // All policies may reconsider together. A pose image is shared; each grid copies and groups its points.
        var perceptionCost = RuleWorkBudget.SaturatingAdd(left: imagePoints, right: RuleWorkBudget.SaturatingMultiply(left: 2, right: gridPoints));

        return new WorldRuleWorkBudget(
            RuleRows: rules.Length,
            InteractionRows: interactions.Length,
            EvaluationSlots: slots,
            WorkUnitsPerTick: RuleWorkBudget.SaturatingAdd(left: RuleWorkBudget.SaturatingAdd(left: work, right: flockCost), right: perceptionCost),
            FlockAffinityWorkUnitsPerTick: flockCost,
            DecisionImagePointsPerTick: imagePoints,
            DecisionGridBuildsPerTick: decisionScales.Count,
            DecisionGridPointsPerTick: gridPoints
        );
    }

    /// <summary>Lists every contributor line, costliest first, then by name.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Contributors(definition, WorldRuleCompiler.CompileAll(definition), WorldRuleCompiler.CompileAllInteractions(definition));
    }

    internal static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var context = WorldRuleCompiler.Context(definition: definition);
        var contributors = Lines(definition: definition, context: context, rules: rules, interactions: interactions);

        contributors.Sort(comparison: static (left, right) => {
            var byWork = right.WorkUnits.CompareTo(value: left.WorkUnits);

            return ((byWork != 0) ? byWork : string.CompareOrdinal(strA: left.Name, strB: right.Name));
        });

        return contributors;
    }

    /// <summary>Lists every rule whose gate pins a cell to an empty range, with that cell.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ContradictoryGates(WorldRuleCompiler.CompileAll(definition));
    }

    internal static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(CompiledWorldRule[] rules) {
        var result = new List<(string, string)>();

        foreach (var rule in rules) {
            foreach (var pinned in RuleWorkBudget.PinnedCells(gate: rule.Gate, contradictory: out _)) {
                if (pinned.IsEmpty) {
                    result.Add(item: (rule.Name, pinned.Cell));
                }
            }
        }

        return result;
    }

    /// <summary>Returns the work units an expression costs against a document.</summary>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, WorldDefinition definition) => RuleWorkBudget.ExpressionCost(tokens: tokens, context: WorldRuleCompiler.Context(definition: definition));

    private static long Multiplier(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule rule) {
        if (rule.Interaction is { } interaction) {
            var others = Math.Max(val1: 0, val2: (definition.Population.Capacity - 1));

            return ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance)
                ? ((long)definition.Population.Capacity * ((interaction.Neighbours > 0) ? Math.Min(val1: interaction.Neighbours, val2: others) : others))
                : definition.Population.Capacity);
        }

        return RuleWorkBudget.ForEachCount(rule: rule, context: context);
    }

    private static List<(CompiledRule Rule, long Multiplier)> Multiplied(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var result = new List<(CompiledRule, long)>(capacity: (rules.Length + interactions.Length));

        foreach (var rule in rules) { result.Add(item: (rule, Multiplier(definition: definition, context: context, rule: rule))); }
        foreach (var interaction in interactions) { result.Add(item: (interaction, Multiplier(definition: definition, context: context, rule: interaction))); }

        return result;
    }

    private static List<RuleWorkContributor> Lines(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var contributors = new List<RuleWorkContributor>(capacity: (rules.Length + interactions.Length));

        foreach (var rule in rules) {
            contributors.Add(item: RuleWorkBudget.Contributor(rule: rule, multiplier: Multiplier(definition: definition, context: context, rule: rule), isInteraction: false, context: context));
        }
        foreach (var interaction in interactions) {
            contributors.Add(item: RuleWorkBudget.Contributor(rule: interaction, multiplier: Multiplier(definition: definition, context: context, rule: interaction), isInteraction: true, context: context));
        }

        return contributors;
    }

    private static long FlockAffinityCost(WorldDefinition definition, WorldRuleCompileContext context) {
        var maximum = 0L;

        foreach (var kit in definition.Kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } profile) { continue; }

                var compiled = new CompiledWorldFlockAffinities(profile, definition);

                maximum = Math.Max(val1: maximum, val2: RuleWorkBudget.SaturatingMultiply(left: profile.MaxNeighbors, right: compiled.WorkUnitsPerNeighbor));
            }
        }

        // Initial samples and explicit producer changes may align every observer, regardless of normal cadence.
        return RuleWorkBudget.SaturatingMultiply(left: definition.Population.Capacity, right: maximum);
    }
}
