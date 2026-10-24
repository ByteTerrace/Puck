using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class DecisionDisclosureTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalEvaluationCannotControlATransformOutsideItsAudience(bool confined) {
        var visibility = new StateVisibility(Readers: ["seat1"]);
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "items"), Kind: CellKind.Int,
            Capacity: 4, Domain: new StateDomain.Keys(), Visibility: confined ? visibility : null,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Int(value: 1L))]);
        var secret = new WorldStateRow(Name: CellName.Parse(candidate: "secret"), Kind: CellKind.Int,
            Visibility: visibility, Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 1L))]);
        var rule = new WorldRule(Name: CellName.Parse(candidate: "choose"), Effects: [],
            Locals: [new RuleLocal(Name: CellName.Parse(candidate: "probe"), Expression: ExpressionProgram.Parse(text: "1 / (secret + 1)"))],
            Decision: new WorldDecision(PeriodSeconds: 0.01m, ScoreKind: CellKind.Int,
                Options: [new WorldDecisionOption(Name: CellName.Parse(candidate: "sort"),
                    Score: ExpressionProgram.Parse(text: "1"),
                    Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: "items", By: [new SortKey(Row: "items")]))])]));
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [row, secret]), Rules: [rule]);
        if (confined) {
            Assert.Single(collection: WorldFactsCompiler.CompileAll(definition: definition));
        } else {
            var error = Assert.Throws<RuleException>(() => WorldFactsCompiler.CompileAll(definition: definition));
            Assert.Contains(expectedSubstring: "TransformWidensAudience", actualString: error.Message);
        }
    }
}
