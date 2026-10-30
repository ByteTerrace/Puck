using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A world rule's <c>maxFirings</c> reaches the work sheet and its read-back derivation, a pool sweep is
/// priced at its pool's capacity, and a decision refuses the bound.</summary>
public sealed class WorldRuleMaxFiringsLawTests {
    private static WorldDefinition Document(params WorldRule[] rules) => new(
        StateRaw: new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "score"), CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "many"), CellKind.Int, Capacity: 64, Cells: [new StateCell(CellName.Parse(candidate: "0"), CellValue.Int(value: 0L))]),
            ],
            Records: [new StateRecord(Name: CellName.Parse(candidate: "piece"), Fields: [
                new StatePoolField(Name: CellName.Parse(candidate: "count"), Kind: CellKind.Int, Default: CellValue.Int(value: 0L)),
            ])],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "pieces"), Record: CellName.Parse(candidate: "piece"), Capacity: 6)]
        ),
        Rules: rules
    );
    private static ActionEffect Score => new ActionEffect.AddState(State: "score", Value: 1m);

    [Fact]
    public void ABoundSweepIsPricedAndDescribedAtItsFirings() {
        var definition = Document(
            new WorldRule(Name: CellName.Parse(candidate: "sweep"), Effects: [Score], ForEach: "many", MaxFirings: 3),
            new WorldRule(Name: CellName.Parse(candidate: "visit"), Effects: [Score],
                PoolForEach: new RulePoolIteration(Pool: "pieces", Binding: CellName.Parse(candidate: "piece")), MaxFirings: 2)
        );
        var compilation = WorldRuleCompilation.Compile(definition: definition);
        var lines = WorldRuleWorkBudget.Contributors(compilation: compilation);
        var sweep = lines.Single(predicate: static line => (line.Name == "sweep"));
        var visit = lines.Single(predicate: static line => (line.Name == "visit"));

        Assert.Equal(expected: (64L, 3L), actual: (sweep.Multiplier, sweep.Firings));
        Assert.Equal(expected: (6L, 2L), actual: (visit.Multiplier, visit.Firings));
        Assert.Equal(expected: (3L * sweep.Cost.Effects), actual: sweep.FiringUnits);
        Assert.Contains(expectedSubstring: "maxFirings=3", actualString: WorldRuleWorkBudget.DescribeMultiplier(definition: definition, rule: compilation.Rules[0]));
        Assert.Contains(expectedSubstring: "pool sweep over 'pieces' capacity=6, firings priced at maxFirings=2", actualString: WorldRuleWorkBudget.DescribeMultiplier(definition: definition, rule: compilation.Rules[1]));
    }
    [Fact]
    public void ADecisionRefusesTheBound() {
        var rule = new WorldRule(Name: CellName.Parse(candidate: "choose"), Effects: [], ForEach: "many", MaxFirings: 1,
            Decision: new WorldDecision(PeriodSeconds: 0.01m, ScoreKind: CellKind.Int,
                Options: [new WorldDecisionOption(Name: CellName.Parse(candidate: "add"), Score: ExpressionProgram.Parse(text: "1"), Effects: [Score])]));
        var error = Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: Document(rule)));

        Assert.Contains(expectedSubstring: "maxFirings", actualString: error.Message);
    }
}
