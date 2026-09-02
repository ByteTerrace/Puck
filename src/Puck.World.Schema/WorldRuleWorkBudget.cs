namespace Puck.World;

/// <summary>The statically derived worst-case work admitted for one world-rule tick.</summary>
/// <param name="RuleRows">The authored ordinary-rule count.</param>
/// <param name="InteractionRows">The authored interaction count.</param>
/// <param name="EvaluationSlots">The greatest number of rule/interaction bindings evaluated in one tick.</param>
/// <param name="WorkUnitsPerTick">The conservative token/effect/candidate-visit total for those bindings.</param>
public readonly record struct WorldRuleWorkBudget(int RuleRows, int InteractionRows, long EvaluationSlots, long WorkUnitsPerTick) {
    /// <summary>Derives the conservative per-tick work sheet from a validated definition.</summary>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = WorldRuleCompiler.CompileAll(definition: definition);
        var interactions = WorldRuleCompiler.CompileAllInteractions(definition: definition);
        var slots = 0L;
        var work = 0L;

        foreach (var rule in rules) {
            var multiplier = ((rule.ForEach is { } rowName)
                ? (WorldDefinitionRows.FindStateRow(rows: definition.State, name: rowName)?.Capacity ?? WorldStateCapacity.MaxCellsPerRow)
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

        return new WorldRuleWorkBudget(
            RuleRows: rules.Length,
            InteractionRows: interactions.Length,
            EvaluationSlots: slots,
            WorkUnitsPerTick: work
        );
    }

    private static long RuleCost(CompiledWorldRule rule, WorldDefinition definition) {
        var cost = 1L;

        foreach (var token in rule.Gate) {
            cost = SaturatingAdd(left: cost, right: OperandCost(operand: token.Left, definition: definition));
            if (token.Comparand is { } comparand) {
                cost = SaturatingAdd(left: cost, right: OperandCost(operand: comparand, definition: definition));
            }
        }
        foreach (var effect in rule.Effects) {
            cost = SaturatingAdd(left: cost, right: EffectCost(effect: effect, definition: definition));
        }

        return cost;
    }

    private static long EffectCost(CompiledWorldEffect effect, WorldDefinition definition) {
        var cost = effect.Kind switch {
            WorldRuleEffectKind.Write or WorldRuleEffectKind.Countdown or WorldRuleEffectKind.RemoveStateCell or WorldRuleEffectKind.ScheduleState => 512L,
            WorldRuleEffectKind.Generate => 1_024L,
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

    private static long EffectsCost(CompiledWorldEffect[] effects, WorldDefinition definition) {
        var cost = 0L;
        foreach (var effect in effects) {
            cost = SaturatingAdd(left: cost, right: EffectCost(effect: effect, definition: definition));
        }
        return cost;
    }

    private static long OperandCost(CompiledWorldOperand operand, WorldDefinition definition) {
        if (operand.Kind is WorldRuleFactKind.Reduction or WorldRuleFactKind.ArgBody) {
            return (WorldDefinitionRows.FindStateRow(rows: definition.State, name: operand.Row ?? string.Empty)?.Capacity
                ?? WorldStateCapacity.MaxCellsPerRow
            );
        }
        if (operand.Kind is WorldRuleFactKind.Nearest or WorldRuleFactKind.RegionOccupancy) {
            return definition.Population.Capacity;
        }

        return 1L;
    }

    private static long SaturatingAdd(long left, long right) => ((left > (long.MaxValue - right)) ? long.MaxValue : (left + right));
    private static long SaturatingMultiply(long left, long right) => ((left == 0L) || (right == 0L))
        ? 0L
        : ((left > (long.MaxValue / right)) ? long.MaxValue : (left * right));
}
