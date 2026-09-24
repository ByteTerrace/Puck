using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A rule's needs are read off its own declarations: the host-owned rows its reads resolve to, whether it
/// reads the tick, and which of its arms cannot be rewound.</summary>
public sealed class RuleNeedsLawTests {
    [Fact]
    public void ARuleThatReadsNeitherTheTickNorAHostOwnedRowNeedsNothing() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.Greater,
                State: "score",
                Value: 0m
            ),
            name: "plain"
        ));

        Assert.Empty(collection: compiled.Needs.Facets);
        Assert.Empty(collection: compiled.Needs.HostOwnedRows);
        Assert.False(condition: compiled.Needs.Irreversible);
        Assert.False(condition: compiled.Needs.ReadsTick);
        Assert.False(condition: compiled.Needs.Volatile);
    }
    [Fact]
    public void AReadOfTheTickMarksTheRuleVolatile() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.GreaterOrEqual,
                State: RuleFacts.Tick,
                Value: 30m
            ),
            name: "timed"
        ));

        Assert.True(condition: compiled.Needs.ReadsTick);
        Assert.True(condition: compiled.Needs.Volatile);
    }
    [Fact]
    public void AReadThatResolvesToAHostOwnedRowRecordsItsOrdinal() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    State: $"{RuleFacts.ReducePrefix}count:{RulesFixture.HostField}",
                    Value: 0m
                ),
                name: "hosted"
            )
        );

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: RulesFixture.HostField
        ));
        Assert.Equal(
            actual: compiled.Needs.HostOwnedRows,
            expected: [handle.Ordinal]
        );
        Assert.True(condition: compiled.Needs.Volatile);
    }
    [Fact]
    public void AnIrreversibleArmIsMarkedForDeferralToCommit() {
        var compiled = RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [
                    new ActionEffect.SetState(
                        Key: "a",
                        State: "codes",
                        Value: 1m
                    ),
                    new IrreversibleFixture.Stamp(),
                ],
                name: "queued"
            )
        );

        Assert.True(condition: compiled.Needs.Irreversible);
        Assert.Equal(
            actual: compiled.Effects[1].Needs,
            expected: EffectNeeds.Irreversible
        );
    }
    [Fact]
    public void ARuleNeedsBuilderTakesTheCompiledFactType() {
        var method = typeof(RuleNeedsBuilder).GetMethod(name: nameof(RuleNeedsBuilder.AddFact));

        Assert.NotNull(@object: method);
        Assert.Equal(
            actual: method!.GetParameters()[0].ParameterType,
            expected: typeof(ICompiledFact)
        );
    }
}
