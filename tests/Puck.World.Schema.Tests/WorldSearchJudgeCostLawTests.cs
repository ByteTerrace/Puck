using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a judge run costs what the tick's own work sheet would charge the same rules, so
/// judge rules whose gates pin one cell to values that exclude each other cost the costliest of them and not their
/// sum. A judge that priced them by summing would refuse a job the arena can afford.</summary>
public sealed class WorldSearchJudgeCostLawTests {
    private static WorldRule Rule(string name, long phase) => new(
        CellName.Parse(candidate: name),
        [new ActionEffect.AddState(
                State: "count",
                Value: 1m
            )],
        ForEach: "many",
        Gate: new ActionPredicate.CompareState(
            State: "phase",
            Comparison: ExpressionOp.Equal,
            Value: phase
        )
    );
    private static WorldStateRow Slot(string name) => new(
        CellName.Parse(candidate: name),
        CellKind.Int,
        Cells: [new StateCell(
                WorldStateRow.SlotKey,
                CellValue.Int(value: 0L)
            )]
    );
    private static long Cost(params WorldRule[] rules) {
        var definition = new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [Slot(name: "phase"), Slot(name: "count"), new WorldStateRow(
                    CellName.Parse(candidate: "many"),
                    CellKind.Int,
                    Capacity: 64,
                    Cells: [new StateCell(
                            CellName.Parse(candidate: "0"),
                            CellValue.Int(value: 0L)
                        )]
                )]),
            Rules: rules
        );

        return WorldSearchCompilation.JudgeCost(
            context: WorldFactsCompiler.Context(definition: definition),
            judge: WorldSearchCompilation.JudgeRules(rules: WorldFactsCompiler.CompileAll(definition: definition))
        ).Units;
    }

    [Fact]
    public void JudgeRulesPinningOneCellToExclusiveValuesCostTheCostliestRatherThanTheSum() {
        var one = Rule(
            name: "one",
            phase: 1
        );
        var two = Rule(
            name: "two",
            phase: 2
        );
        var alone = Cost(one);
        var both = Cost(
            one,
            two
        );

        Assert.True(
            condition: (alone > 1L),
            userMessage: alone.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
        );
        // Checks sum and the firing is shared, so two exclusive rules cost strictly less than twice one of them.
        Assert.True(
            condition: (both < (2L * alone)),
            userMessage: $"one={alone} both={both}"
        );
        Assert.True(
            condition: (both > alone),
            userMessage: $"one={alone} both={both}"
        );
    }
}
