using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A rule that reads a line of sight anywhere it can hold a comparison asks for the solid field the sight
/// reads: in its gate under any composition of predicates, or in a conditional effect's condition or branches, the
/// form a <c>puck test</c> expectation lowers to.</summary>
public sealed class WorldTargetSelectionLawTests {
    private static readonly ActionPredicate Sight = new ActionPredicate.CompareState(
        Comparison: ExpressionOp.Equal,
        State: StateChannelRef.Parse(spelling: "$los:body:0:body:1"),
        Value: 1m
    );
    private static readonly ActionEffect Mark = new ActionEffect.SetState(State: "seen", Value: 1m);

    [Fact]
    public void ARuleReadingASightLineInAnyPredicateOrBranchAsksForTheSolidField() {
        Assert.False(condition: Requires(effects: [Mark]));
        Assert.True(condition: Requires(effects: [Mark], gate: new ActionPredicate.Any(Predicates: [Sight])));
        Assert.True(condition: Requires(effects: [Mark], gate: new ActionPredicate.Not(Predicate: Sight)));
        Assert.True(condition: Requires(effects: [new ActionEffect.If(Condition: Sight, Then: [Mark])]));
        Assert.True(condition: Requires(effects: [new ActionEffect.If(Condition: new ActionPredicate.Not(Predicate: Sight), Then: [], Else: [new ActionEffect.If(Condition: Sight, Then: [Mark])])]));
    }

    private static bool Requires(IReadOnlyList<ActionEffect> effects, ActionPredicate? gate = null) =>
        WorldTargetSelection.RequiresLineOfSight(definition: Fixtures.BuildDocument() with {
            Rules = [new WorldRule(Effects: effects, Gate: gate, Name: CellName.Parse(candidate: "sight"))],
        });
}
