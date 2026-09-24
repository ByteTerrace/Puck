using Xunit;

namespace Puck.State.Rules.Tests;

public sealed class UndoRuleLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static RuleException CompileRefusal(IReadOnlyList<Rule> authored, RuleCompileContext? context = null, IReadOnlyList<RuleGroupDeclaration>? groups = null) {
        context ??= RulesFixture.Context();
        var rules = RuleCompiler.CompileAll(context: context, rules: authored);

        return Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileGroups(
            context: context,
            groups: (groups ?? [UndoGroup(member: authored[0].Name)]),
            rules: rules
        ));
    }
    private static RuleGroupDeclaration UndoGroup(CellName member) => new(
        Name: Name(value: "turn"),
        Shape: RuleGroupShape.Staged,
        Steps: [new RuleGroupStep(Rule: member)],
        Undo: new RuleGroupUndo(Rows: [Name(value: "score")], Depth: 4)
    );
    private static (ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, CompiledRuleGroup[] Groups, RuleGroupState State, RuleLatch Latch) ArrangeRuntime(IReadOnlyList<Rule> rules, RuleGroupDeclaration group) {
        var section = EvaluatorFixture.Section();
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.CompileAll(context: context, rules: rules);
        var groups = RuleCompiler.CompileGroups(context: context, groups: [group], rules: compiled);
        var host = EvaluatorFixture.Host(context: context, section: section);

        return (host, new RuleEvaluator(host: host), compiled, groups, new RuleGroupState(), new RuleLatch());
    }
    private static void TickGroup((ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, CompiledRuleGroup[] Groups, RuleGroupState State, RuleLatch Latch) runtime, ulong tick) {
        runtime.Host.Advance(engineTick: tick, tick: tick);
        _ = runtime.Evaluator.EvaluateGroups(runtime.Rules, runtime.Groups, runtime.State, runtime.Latch, stepTicks: 1UL);
    }

    [Fact]
    public void AnUndoGroupRejectsAnIrreversibleMember() {
        var exception = CompileRefusal(
            authored: [new Rule(Name(value: "stamp"), [new IrreversibleFixture.Stamp()])],
            context: IrreversibleFixture.Context()
        );

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    [Fact]
    public void AnUndoGroupRejectsAHostOwnedRow() {
        var exception = CompileRefusal(
            authored: [RulesFixture.Rule(name: "write", effects: [new ActionEffect.SetState(State: RulesFixture.HostField, Key: "0", Value: 1m)])],
            groups: [new RuleGroupDeclaration(
                Name: Name(value: "turn"),
                Shape: RuleGroupShape.Staged,
                Steps: [new RuleGroupStep(Rule: Name(value: "write"))],
                Undo: new RuleGroupUndo(Rows: [Name(value: RulesFixture.HostField)], Depth: 1)
            )]
        );

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    [Fact]
    public void RewindInsideItsOwnGroupIsRejected() {
        var rewind = RulesFixture.Rule(name: "rewind", effects: [new ActionEffect.RewindGroup(Group: Name(value: "turn"))]);
        var exception = CompileRefusal(authored: [rewind], groups: [UndoGroup(member: rewind.Name)]);

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    [Fact]
    public void NestedRewindIsRejected() {
        var condition = new ActionPredicate.CompareState(State: "score", Comparison: ExpressionOp.GreaterOrEqual, Value: 0m);
        var member = RulesFixture.Rule(name: "step");
        var rewind = RulesFixture.Rule(name: "rewind", effects: [new ActionEffect.If(
            Condition: condition,
            Then: [new ActionEffect.RewindGroup(Group: Name(value: "turn"))]
        )]);
        var exception = CompileRefusal(authored: [member, rewind]);

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    [Fact]
    public void RewindBesideAnotherEffectIsRejected() {
        var member = RulesFixture.Rule(name: "step");
        var rewind = RulesFixture.Rule(name: "rewind", effects: [
            new ActionEffect.RewindGroup(Group: Name(value: "turn")),
            new ActionEffect.SetState(State: "score", Value: 0m),
        ]);
        var exception = CompileRefusal(authored: [member, rewind]);

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    [Fact]
    public void RewindNamingAGroupWithoutUndoIsRejected() {
        var member = RulesFixture.Rule(name: "step");
        var rewind = RulesFixture.Rule(name: "rewind", effects: [new ActionEffect.RewindGroup(Group: Name(value: "other"))]);
        var exception = CompileRefusal(authored: [member, rewind]);

        Assert.Equal(RuleRefusal.RuleGroupMalformed, exception.Refusal);
    }
    // A rewind is priced by the work of restoring its group's widest turn, which grows with the rows the group retains
    // and not with how many turns it keeps.
    [InlineData(1)]
    [InlineData(64)]
    [Theory]
    public void ARewindIsPricedByRestoringItsGroupsWidestTurn(int depth) {
        var section = EvaluatorFixture.Section();
        var context = EvaluatorFixture.Context(section: section);
        var member = RulesFixture.Rule(name: "step", effects: [new ActionEffect.SetState(State: "score", Value: 7m)]);
        var rewind = RulesFixture.Rule(name: "rewind", effects: [new ActionEffect.RewindGroup(Group: Name(value: "turn"))]);
        var compiled = RuleCompiler.CompileAll(context: context, rules: [member, rewind]);
        var group = Assert.Single(collection: RuleCompiler.CompileGroups(context: context, groups: [UndoGroup(member: member.Name) with {
            Undo = new RuleGroupUndo(Rows: [Name(value: "score")], Depth: depth),
        }], rules: compiled));
        var effect = Assert.IsType<RewindGroupEffect>(@object: Assert.Single(collection: compiled[1].Effects));

        Assert.Equal(
            expected: RuleWork.Known(units: StateArena.EstimateRewindWork(
                catalog: context.Catalog,
                layout: ArenaLayout.Build(context.Catalog, context.Section),
                plan: group.Undo!,
                plans: [group.Undo!]
            )),
            actual: effect.Cost(context: context)
        );
    }
    [Fact]
    public void AWorkflowRetainsItsTwoStepsAsOneTurn() {
        var first = RulesFixture.Rule(name: "first", effects: [new ActionEffect.SetState(State: "score", Value: 7m)]);
        var second = RulesFixture.Rule(name: "second", effects: [new ActionEffect.SetState(State: "other", Value: 9m)]);
        var group = new RuleGroupDeclaration(
            Name: Name(value: "turn"),
            Shape: RuleGroupShape.Staged,
            Steps: [new RuleGroupStep(first.Name), new RuleGroupStep(second.Name)],
            Undo: new RuleGroupUndo(Rows: [Name(value: "score"), Name(value: "other")], Depth: 2)
        );
        var runtime = ArrangeRuntime(group: group, rules: [first, second]);

        TickGroup(runtime: runtime, tick: 0UL);
        var unrelated = runtime.Host.Arena.BeginScope();

        Assert.True(condition: runtime.Host.Arena.TryWrite(
            rowOrdinal: EvaluatorFixture.Ordinal(host: runtime.Host, row: "third"),
            key: runtime.Host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 5L,
            write: StateWriteKind.Set,
            reason: out _
        ));
        runtime.Host.Arena.Commit(mark: unrelated);
        TickGroup(runtime: runtime, tick: 1UL);
        Assert.True(condition: runtime.Host.Arena.TryRewindGroup(group: "turn", reason: out var reason), userMessage: reason);
        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "score"));
        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "other"));
        Assert.Equal(5L, EvaluatorFixture.Cell(runtime.Host, "third"));
    }
    [Fact]
    public void AFixpointRetainsThePassesUntilItSettles() {
        var raise = new Rule(
            Name: Name(value: "raise"),
            Effects: [new ActionEffect.AddState(State: "score", Value: 1m)],
            Gate: new ActionPredicate.CompareState(State: "score", Comparison: ExpressionOp.Less, Value: 2m)
        );
        var group = new RuleGroupDeclaration(
            Name: Name(value: "turn"),
            Shape: RuleGroupShape.Fixpoint,
            Steps: [new RuleGroupStep(raise.Name)],
            Undo: new RuleGroupUndo(Rows: [Name(value: "score")], Depth: 2)
        );
        var runtime = ArrangeRuntime(group: group, rules: [raise]);

        TickGroup(runtime: runtime, tick: 0UL);
        TickGroup(runtime: runtime, tick: 1UL);
        TickGroup(runtime: runtime, tick: 2UL);
        Assert.Equal(2L, EvaluatorFixture.Cell(runtime.Host, "score"));
        Assert.True(condition: runtime.Host.Arena.TryRewindGroup(group: "turn", reason: out var reason), userMessage: reason);
        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "score"));
    }
    [Fact]
    public void IdleFixpointPassesDoNotEvictAMeaningfulTurn() {
        var raise = new Rule(
            Name: Name(value: "raise"),
            Effects: [new ActionEffect.AddState(State: "score", Value: 1m)],
            Gate: new ActionPredicate.CompareState(State: "score", Comparison: ExpressionOp.Less, Value: 1m)
        );
        var group = new RuleGroupDeclaration(
            Name: Name(value: "turn"),
            Shape: RuleGroupShape.Fixpoint,
            Steps: [new RuleGroupStep(raise.Name)],
            Undo: new RuleGroupUndo(Rows: [Name(value: "score")], Depth: 2)
        );
        var runtime = ArrangeRuntime(group: group, rules: [raise]);

        for (ulong tick = 0; (tick < 8); tick++) {
            TickGroup(runtime: runtime, tick: tick);
        }
        Assert.True(condition: runtime.Host.Arena.TryRewindGroup(group: "turn", reason: out var reason), userMessage: reason);
        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "score"));
    }
    [Fact]
    public void RewindSuppressesItsTargetGroupForTheRestOfTheTick() {
        var step = RulesFixture.Rule(name: "step", effects: [new ActionEffect.SetState(State: "score", Value: 7m)]);
        var rewind = RulesFixture.Rule(name: "rewind", effects: [new ActionEffect.RewindGroup(Group: Name(value: "turn"))]);
        var runtime = ArrangeRuntime([step, rewind], UndoGroup(member: step.Name));

        TickGroup(runtime: runtime, tick: 0UL);
        runtime.Host.Advance(engineTick: 1UL, tick: 1UL);
        Assert.True(condition: runtime.Evaluator.Evaluate(rules: [runtime.Rules[1]], latch: runtime.Latch, stepTicks: 1UL));
        _ = runtime.Evaluator.EvaluateGroups(runtime.Rules, runtime.Groups, runtime.State, runtime.Latch, stepTicks: 1UL);
        runtime.Host.Advance(engineTick: 1UL, tick: 1UL);
        _ = runtime.Evaluator.EvaluateGroups(runtime.Rules, runtime.Groups, runtime.State, runtime.Latch, stepTicks: 1UL);

        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "score"));
        Assert.False(condition: runtime.Host.Arena.UndoTurnPending(group: "turn"));
        TickGroup(runtime: runtime, tick: 2UL);
        Assert.Equal(7L, EvaluatorFixture.Cell(runtime.Host, "score"));
    }
    [Fact]
    public void ALaterWriteToAnOwnedRowMakesTheNewestTurnUnrewindable() {
        var step = RulesFixture.Rule(name: "step", effects: [new ActionEffect.SetState(State: "score", Value: 7m)]);
        var runtime = ArrangeRuntime([step], UndoGroup(member: step.Name));

        TickGroup(runtime: runtime, tick: 0UL);
        var mark = runtime.Host.Arena.BeginScope();

        Assert.True(condition: runtime.Host.Arena.TryWrite(
            rowOrdinal: EvaluatorFixture.Ordinal(host: runtime.Host, row: "score"),
            key: runtime.Host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 8L,
            write: StateWriteKind.Set,
            reason: out _
        ));
        runtime.Host.Arena.Commit(mark: mark);

        Assert.False(condition: runtime.Host.Arena.TryRewindGroup(group: "turn", reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "outside");
    }
    [Fact]
    public void ARewindRuleUsesItsOwnGateAsAuthority() {
        var step = RulesFixture.Rule(name: "step", effects: [new ActionEffect.SetState(State: "score", Value: 7m)]);
        var rewind = new Rule(
            Name: Name(value: "rewind"),
            Effects: [new ActionEffect.RewindGroup(Group: Name(value: "turn"))],
            Gate: new ActionPredicate.CompareState(State: "flag", Comparison: ExpressionOp.Equal, Value: 1m),
            Mode: ActionTriggerMode.Edge
        );
        var runtime = ArrangeRuntime([step, rewind], UndoGroup(member: step.Name));

        TickGroup(runtime: runtime, tick: 0UL);
        runtime.Host.Advance(engineTick: 1UL, tick: 1UL);
        _ = runtime.Evaluator.Evaluate(rules: [runtime.Rules[1]], latch: runtime.Latch, stepTicks: 1UL);
        Assert.Equal(7L, EvaluatorFixture.Cell(runtime.Host, "score"));

        Assert.True(condition: runtime.Host.Arena.TryWrite(
            rowOrdinal: EvaluatorFixture.Ordinal(host: runtime.Host, row: "flag"),
            key: runtime.Host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 1L,
            write: StateWriteKind.Set,
            reason: out _
        ));
        runtime.Host.Advance(engineTick: 2UL, tick: 2UL);
        _ = runtime.Evaluator.Evaluate(rules: [runtime.Rules[1]], latch: runtime.Latch, stepTicks: 1UL);
        Assert.Equal(0L, EvaluatorFixture.Cell(runtime.Host, "score"));
    }
}
