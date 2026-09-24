using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>What a compiled rule group carries: its members, its shape, the rows a fixpoint pass watches, its pass
/// ceiling, its trigger, and the per-step policy a staged cursor applies.</summary>
public sealed class RuleGroupLawTests {
    private static (CompiledRule[] Rules, RuleCompileContext Context) Rules() {
        var context = RulesFixture.Context();
        var rules = RuleCompiler.CompileAll(
            context: context,
            rules: [
                RulesFixture.Rule(
                    effects: [new ActionEffect.AddState(
                        State: "score",
                        Value: 1m
                    )],
                    name: "settle"
                ),
                RulesFixture.Rule(
                    effects: [new ActionEffect.SetState(
                        Key: "a",
                        State: "codes",
                        Value: 1m
                    )],
                    name: "mark"
                ),
            ]
        );

        return (rules, context);
    }

    // A trigger's expression key mints an implicit binding, exactly as a rule's gate does; the group carries it,
    // because the compiled trigger addresses that binding's slot.
    [Fact]
    public void AGroupCarriesTheBindingsItsTriggerMinted() {
        var (rules, context) = Rules();
        var group = Assert.Single(collection: RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "armed"),
                Passes: 2,
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))],
                Trigger: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.Greater,
                    Key: "$expr:1+1",
                    State: "codes",
                    Value: 0m
                )
            )],
            rules: rules
        ));
        var binding = Assert.Single(collection: group.Locals);

        Assert.NotEmpty(collection: binding.Expression);
        Assert.NotEmpty(collection: group.Trigger);
        Assert.NotNull(@object: group.Needs);

        var untriggered = Assert.Single(collection: RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "always"),
                Passes: 2,
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))]
            )],
            rules: rules
        ));

        Assert.Empty(collection: untriggered.Locals);
    }
    // A group's Needs are its trigger's, read off the trigger's own scope, and what a host admits a group by. A
    // triggerless group needs nothing of a host, so it is admitted everywhere.
    [Fact]
    public void AGroupsNeedsAreItsTriggersAndAHostIsAdmittedAgainstThem() {
        var (rules, context) = Rules();
        var timed = Assert.Single(collection: RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "timed"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))],
                Trigger: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    State: RuleFacts.Tick,
                    Value: 30m
                )
            )],
            rules: rules
        ));

        Assert.True(condition: timed.Needs.ReadsTick);
        Assert.True(condition: timed.Needs.Volatile);

        var hosted = Assert.Single(collection: RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "hosted"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))],
                Trigger: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    State: $"{RuleFacts.ReducePrefix}count:{RulesFixture.HostField}",
                    Value: 0m
                )
            )],
            rules: rules
        ));

        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: RulesFixture.HostField
        ));
        Assert.Equal(
            actual: hosted.Needs.HostOwnedRows,
            expected: [handle.Ordinal]
        );

        var untriggered = Assert.Single(collection: RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "always"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))]
            )],
            rules: rules
        ));

        Assert.False(condition: untriggered.Needs.ReadsTick);
        Assert.Empty(collection: untriggered.Needs.Facets);
        Assert.Empty(collection: untriggered.Needs.HostOwnedRows);
    }
    [Fact]
    public void AFixpointGroupCarriesItsMembersWriteSetAndPassCeiling() {
        var (rules, context) = Rules();
        var groups = RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "settleLoop"),
                Passes: 4,
                Shape: RuleGroupShape.Fixpoint,
                Steps: [
                    new RuleGroupStep(Rule: CellName.Parse(candidate: "settle")),
                    new RuleGroupStep(Rule: CellName.Parse(candidate: "mark")),
                ]
            )],
            rules: rules
        );
        var group = Assert.Single(collection: groups);

        Assert.Equal(
            actual: group.Shape,
            expected: RuleGroupShape.Fixpoint
        );
        Assert.Equal(
            actual: group.Members,
            expected: [0, 1]
        );
        Assert.Equal(
            actual: group.Passes,
            expected: 4
        );
        Assert.Equal(
            actual: group.TerminalStep,
            expected: -1
        );
        Assert.Equal(
            actual: group.WriteRows.Length,
            expected: 2
        );
        Assert.Equal(
            actual: group.WriteRows,
            expected: group.WriteRows.OrderBy(keySelector: static ordinal => ordinal).ToArray()
        );
    }
    [Fact]
    public void AStagedGroupKeepsItsStepPolicyAndNamesItsTerminalStep() {
        var (rules, context) = Rules();
        var groups = RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "flow"),
                Shape: RuleGroupShape.Staged,
                Steps: [
                    new RuleGroupStep(
                        OnRefusal: RuleGroupStepPolicy.Skip,
                        Rule: CellName.Parse(candidate: "settle")
                    ),
                    new RuleGroupStep(Rule: CellName.Parse(candidate: "mark")),
                ]
            )],
            rules: rules
        );
        var group = Assert.Single(collection: groups);

        Assert.Equal(
            actual: group.Policies,
            expected: [RuleGroupStepPolicy.Skip, RuleGroupStepPolicy.Stall]
        );
        Assert.Equal(
            actual: group.TerminalStep,
            expected: 1
        );
    }
    [Fact]
    public void AGroupsTriggerCompilesToAGateProgram() {
        var (rules, context) = Rules();
        var groups = RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "armed"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))],
                Trigger: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.Greater,
                    State: "score",
                    Value: 0m
                )
            )],
            rules: rules
        );
        var group = Assert.Single(collection: groups);

        Assert.Single(collection: group.Trigger);
        Assert.Equal(
            actual: group.Trigger[0].Op,
            expected: GateOp.Compare
        );
    }
    [Fact]
    public void ARuleBelongsToAtMostOneGroup() {
        var (rules, context) = Rules();
        var exception = Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileGroups(
            context: context,
            groups: [
                new RuleGroupDeclaration(
                    Name: CellName.Parse(candidate: "first"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))]
                ),
                new RuleGroupDeclaration(
                    Name: CellName.Parse(candidate: "second"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))]
                ),
            ],
            rules: rules
        ));

        Assert.Equal(
            actual: exception.Refusal,
            expected: RuleRefusal.RuleGroupMalformed
        );
    }
    [Fact]
    public void APassCeilingOutsideItsBoundsIsRefusedByName() {
        var (rules, context) = Rules();
        var exception = Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "runaway"),
                Passes: (RuleGroupCapacity.MaxPasses + 1),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "settle"))]
            )],
            rules: rules
        ));

        Assert.Equal(
            actual: exception.Refusal,
            expected: RuleRefusal.RuleGroupMalformed
        );
    }
    [Fact]
    public void AGroupNamingARuleTheSectionDoesNotDeclareIsRefusedByName() {
        var (rules, context) = Rules();
        var exception = Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileGroups(
            context: context,
            groups: [new RuleGroupDeclaration(
                Name: CellName.Parse(candidate: "ghost"),
                Shape: RuleGroupShape.Fixpoint,
                Steps: [new RuleGroupStep(Rule: CellName.Parse(candidate: "absent"))]
            )],
            rules: rules
        ));

        Assert.Equal(
            actual: exception.Refusal,
            expected: RuleRefusal.RuleGroupMalformed
        );
    }
}
