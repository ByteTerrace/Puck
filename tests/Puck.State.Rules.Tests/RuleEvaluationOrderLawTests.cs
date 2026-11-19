using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>The one ordering a host's tick and the work sheet both read: the rules no group claims first, in document
/// order, then the groups, then the interactions.</summary>
public sealed class RuleEvaluationOrderLawTests {
    private static (CompiledRule[] Rules, CompiledRuleGroup[] Groups, RuleGroupDeclaration[] Declarations) Fixture() {
        var context = RulesFixture.Context();
        var rules = RuleCompiler.CompileAll(
            context: context,
            rules: [
                RulesFixture.Rule(
                    effects: [new ActionEffect.AddState(State: "score", Value: 1m)],
                    name: "first"
                ),
                RulesFixture.Rule(
                    effects: [new ActionEffect.AddState(State: "score", Value: 1m)],
                    name: "grouped"
                ),
                RulesFixture.Rule(
                    effects: [new ActionEffect.AddState(State: "score", Value: 1m)],
                    name: "last"
                ),
            ]
        );
        RuleGroupDeclaration[] declarations = [new RuleGroupDeclaration(
            Name: CellName.Parse(candidate: "loop"),
            Shape: RuleGroupShape.Fixpoint,
            Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "grouped"))]
        )];

        return (rules, RuleCompiler.CompileGroups(context: context, groups: declarations, rules: rules), declarations);
    }

    [Fact]
    public void UngroupedRulesRunInDocumentOrderAndAreTheOnesTheOrderPlacesFirst() {
        var (rules, groups, _) = Fixture();
        var order = RuleEvaluationOrder.Of(groups: groups, rules: rules);

        Assert.Equal(
            actual: order.Ungrouped.Select(selector: static rule => rule.Name),
            expected: ["first", "last"]
        );
        Assert.Equal(
            actual: new RulePlace(Line: 0L, Writer: 0L),
            expected: order.Place(index: 0)
        );
        Assert.Equal(
            actual: new RulePlace(Line: 1L, Writer: 1L),
            expected: order.Place(index: 2)
        );
    }
    [Fact]
    public void AGroupedRuleRunsAnywhereAndItsWritesLandAfterEveryUngroupedRuleAndBeforeEveryInteraction() {
        var (rules, groups, _) = Fixture();
        var order = RuleEvaluationOrder.Of(groups: groups, rules: rules);
        var grouped = order.Place(index: 1);

        Assert.Equal(
            actual: grouped.Line,
            expected: -1L
        );
        Assert.True(condition: grouped.Writer > order.Place(index: 2).Writer);
        Assert.True(condition: grouped.Writer < order.InteractionPlace(index: 0));
        Assert.True(condition: order.InteractionPlace(index: 0) < order.InteractionPlace(index: 1));
    }
    [Fact]
    public void ClaimingByAuthoredNameOrdersRulesAsClaimingByCompiledMembersDoes() {
        var (rules, groups, declarations) = Fixture();
        var byMembers = RuleEvaluationOrder.Of(groups: groups, rules: rules);
        var byName = RuleEvaluationOrder.Named(groups: declarations, rules: rules);

        Assert.Equal(
            actual: byMembers.Ungrouped,
            expected: byName.Ungrouped
        );
        for (var index = 0; (index < rules.Length); index++) {
            Assert.Equal(
                actual: byMembers.Place(index: index),
                expected: byName.Place(index: index)
            );
        }
    }
}
