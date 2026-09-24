using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Deferral belongs to the arm that cannot be rewound, never to the branch or savepoint holding it: a
/// composite stays inside the firing's scope, and the refusal that guards a read of a deferred result follows the
/// arms it holds.</summary>
public sealed class DeferralLawTests {
    private static Rule Branch(IReadOnlyList<ActionEffect> then, IReadOnlyList<ActionEffect>? trailing = null) {
        var effects = new List<ActionEffect> {
            new ActionEffect.If(
                Condition: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.Greater,
                    State: "score",
                    Value: 0m
                ),
                Then: then
            ),
        };

        effects.AddRange(collection: (trailing ?? []));

        return RulesFixture.Rule(
            effects: effects,
            name: "branched"
        );
    }

    [Fact]
    public void ABranchHoldingAnIrreversibleArmIsNotItselfDeferred() {
        var compiled = RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: Branch(then: [
                new ActionEffect.SetState(
                    State: "ratio",
                    Value: 1m
                ),
                new IrreversibleFixture.Stamp(),
            ])
        );

        var branch = Assert.IsType<IfEffect>(@object: Assert.Single(collection: compiled.Effects));

        Assert.Equal(
            actual: branch.Needs,
            expected: EffectNeeds.None
        );
        Assert.Equal(
            actual: branch.Then[1].Needs,
            expected: EffectNeeds.Irreversible
        );
        Assert.True(condition: compiled.Needs.Irreversible);
    }
    [Fact]
    public void ASavepointHoldingAnIrreversibleArmIsNotItselfDeferred() {
        var compiled = RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.Transaction(Effects: [
                        new ActionEffect.SetState(
                            State: "ratio",
                            Value: 1m
                        ),
                        new IrreversibleFixture.Stamp(),
                    ])],
                name: "savepoint"
            )
        );

        var savepoint = Assert.IsType<TransactionEffect>(@object: Assert.Single(collection: compiled.Effects));

        Assert.Equal(
            actual: savepoint.Needs,
            expected: EffectNeeds.None
        );
        Assert.Equal(
            actual: savepoint.Effects[1].Needs,
            expected: EffectNeeds.Irreversible
        );
    }
    [Fact]
    public void ABranchExposesItsArmsSoTheWalkReachesThem() {
        var compiled = RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: Branch(then: [new IrreversibleFixture.Stamp()])
        );
        var branch = Assert.IsType<IfEffect>(@object: Assert.Single(collection: compiled.Effects));

        Assert.Equal(
            actual: branch.Arms.Count,
            expected: 2
        );
        Assert.Same(
            actual: branch.Arms[0],
            expected: branch.Then
        );
        Assert.Same(
            actual: branch.Arms[1],
            expected: branch.Else
        );
    }
    [Fact]
    public void ALaterSiblingThatReadsWhatABranchsIrreversibleArmWritesIsRefused() {
        var failure = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: Branch(
                then: [new IrreversibleFixture.Stamp()],
                trailing: [new ActionEffect.SetState(
                        FromState: "score",
                        State: "cooldown"
                    )]
            )
        ));

        Assert.Equal(
            actual: failure.Refusal,
            expected: RuleRefusal.IrreversibleResultRead
        );
    }
    [Fact]
    public void ALaterArmInsideTheSameBranchThatReadsAnIrreversibleResultIsRefused() {
        var failure = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: Branch(then: [
                new IrreversibleFixture.Stamp(),
                new ActionEffect.SetState(
                    FromState: "score",
                    State: "cooldown"
                ),
            ])
        ));

        Assert.Equal(
            actual: failure.Refusal,
            expected: RuleRefusal.IrreversibleResultRead
        );
    }
    [Fact]
    public void ATopLevelIrreversibleArmDeclaresItsOwnDeferral() {
        var compiled = RuleCompiler.Compile(
            context: IrreversibleFixture.Context(),
            rule: RulesFixture.Rule(
                effects: [
                    new ActionEffect.SetState(
                        State: "ratio",
                        Value: 1m
                    ),
                    new IrreversibleFixture.Stamp(),
                ],
                name: "queued"
            )
        );

        Assert.Equal(
            actual: compiled.Effects[0].Needs,
            expected: EffectNeeds.None
        );
        Assert.Equal(
            actual: compiled.Effects[1].Needs,
            expected: EffectNeeds.Irreversible
        );
    }
}
