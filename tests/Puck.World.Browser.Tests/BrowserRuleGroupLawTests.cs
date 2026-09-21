using Xunit;

using RuleGroupDeclaration = Puck.State.Rules.RuleGroupDeclaration;
using RuleGroupShape = Puck.State.Rules.RuleGroupShape;
using RuleGroupStep = Puck.State.Rules.RuleGroupStep;

namespace Puck.World.Browser.Tests;

/// <summary>THE LAW: a rule group this session cannot compile is refused whole. Its members are claimed by it, so
/// they run neither under the group nor as ordinary rules, and the refusal rides the judged tick's ledger.</summary>
public sealed class BrowserRuleGroupLawTests {
    private const string ScoreRow = "score";

    private static WorldDefinition Document(bool group) =>
        new(
            Rules: [new WorldRule(
                Name: CellName.Parse(candidate: "tally"),
                Effects: [new ActionEffect.AddState(
                        State: ScoreRow,
                        Value: 1m
                    )]
            )],
            RuleGroupsRaw: (group
                ? [new RuleGroupDeclaration(
                    Name: CellName.Parse(candidate: "counting"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [
                        new RuleGroupStep(Rule: CellName.Parse(candidate: "tally")),
                        // No rule carries this name, so the group refuses compilation whole.
                        new RuleGroupStep(Rule: CellName.Parse(candidate: "missing")),
                    ]
                )]
                : null
            ),
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [new WorldStateRow(
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )],
                Kind: CellKind.Int,
                Max: 1000L,
                Min: 0L,
                Name: CellName.Parse(candidate: ScoreRow)
            )])
        );

    [Fact]
    public void ARefusedGroupRunsNeitherItselfNorTheRulesItClaims() {
        var ungrouped = new BrowserSession(definition: Document(group: false));
        var refused = new BrowserSession(definition: Document(group: true));
        var ran = ungrouped.Judge(tick: 1UL);
        var refusedTrace = refused.Judge(tick: 1UL);

        // The control proves the member is a rule that would have written on this tick.
        Assert.Contains(
            collection: ran.Writes,
            filter: static write => string.Equals(
                a: write.Row,
                b: ScoreRow,
                comparisonType: StringComparison.Ordinal
            )
        );
        Assert.Empty(collection: refusedTrace.Writes);
        // Preview admission must not make authored work disappear from the cost report.
        Assert.Equal(ungrouped.CostReport.WorkBudget, refused.CostReport.WorkBudget);
        Assert.Single(refused.CostReport.Contributors);
        Assert.Contains(
            collection: refusedTrace.Refusals,
            filter: static refusal => string.Equals(
                a: refusal.Category,
                b: BrowserSession.AdmissionRefusalCategory,
                comparisonType: StringComparison.Ordinal
            )
        );
    }
}
