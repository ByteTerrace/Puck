using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Transforms cannot publish hidden selectors, membership or live cell values through their results.</summary>
public sealed class TransformDisclosureLawTests {
    private static readonly StateVisibility Private = new(Readers: ["seat1"]);

    private static StateSection WithPolicies(params string[] names) {
        var section = TransformFixture.Section();
        return section with { Rows = section.Rows!.Select(row => names.Contains(row.Name.Value) ? row with { Visibility = Private } : row).ToArray() };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenSelectorsCannotChoosePublicTransformResults(bool liveEnd) {
        var section = WithPolicies(liveEnd ? "rankValue" : "pointer");
        var context = TransformFixture.Context(section: section);
        var transform = liveEnd
            ? (StateTransform)new StateTransform.Transfer(From: "deck", To: "$zones[(rankValue)]", Selector: ZoneSelector.First)
            : new StateTransform.WriteSet(Row: "target", Set: "scores", SetKey: "$cell:pointer:$value", Value: 1L);
        var rule = new Rule(Name: TransformFixture.Name(value: "leak"), Zones: ["hand"], Effects: [new ActionEffect.TransformState(Transform: transform)]);

        var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
        Assert.Contains("TransformWidensAudience", exception.Message);
    }

    [Fact]
    public void PrivateZoneMembershipCannotTransferIntoAPublicZone() {
        var context = TransformFixture.Context(section: WithPolicies("deck"));
        Assert.False(RuleCompiler.TryResolveTransform(new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.First), context, out _, out var reason));
        Assert.Contains("TransformWidensAudience", reason);
    }

    [Fact]
    public void ExplicitRuleWritesAndObserveStillPublishTheirDeclaredResults() {
        var section = WithPolicies("rankValue", "truth");
        var context = TransformFixture.Context(section: section);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(
            Name: TransformFixture.Name(value: "publish"), Mode: ActionTriggerMode.Level,
            Gate: EvaluatorFixture.Compare(row: "rankValue", comparison: ExpressionOp.Equal, value: 0m),
            Effects: [new ActionEffect.SetState(State: "mask", FromState: "rankValue"),
                new ActionEffect.TransformState(Transform: new StateTransform.Observe(Row: "known"))])]);
        var host = TransformFixture.Host(context: context, section: section);
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);

        Assert.Empty(evaluator.Diagnostics());
        Assert.Equal(CellValue.Int(value: 0L), host.Arena.Read(rowOrdinal: TransformFixture.Ordinal(context: context, name: "mask"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey)));
        Assert.Equal("a=4,b=6", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "known")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HiddenControlFlowCannotChooseAPublicTransform(bool branch, bool runtime) {
        var section = runtime ? WithPolicies() : WithPolicies("rankValue");
        var context = TransformFixture.Context(section: section);
        var gate = EvaluatorFixture.Compare(row: "rankValue", comparison: ExpressionOp.Equal, value: 0m);
        var transform = new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]));
        var rule = new Rule(Name: TransformFixture.Name(value: "conditional"), Mode: ActionTriggerMode.Level,
            Gate: branch ? null : gate, Effects: [branch ? new ActionEffect.If(Condition: gate, Then: [transform]) : transform]);
        if (!runtime) {
            var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
            Assert.Contains("TransformWidensAudience", exception.Message);
            return;
        }
        var rules = RuleCompiler.CompileAll(context: context, rules: [rule]);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(host.Arena.TryWriteVisibility(rowOrdinal: TransformFixture.Ordinal(context: context, name: "rankValue"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey), visibility: Private));
        var evaluator = new RuleEvaluator(host: host);
        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);
        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHiddenLiveSelectorRewindsTheWholeRuleFiring(bool liveEnd) {
        var section = WithPolicies();
        var context = TransformFixture.Context(section: section);
        var transform = liveEnd
            ? (StateTransform)new StateTransform.Transfer(From: "deck", To: "$zones[(rankValue)]", Selector: ZoneSelector.First)
            : new StateTransform.WriteSet(Row: "target", Set: "scores", SetKey: "$cell:pointer:$value", Value: 1L);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(
            Name: TransformFixture.Name(value: "leak"), Zones: ["hand"], Mode: ActionTriggerMode.Level,
            Effects: [EvaluatorFixture.Set(row: "mask", value: 7m), new ActionEffect.TransformState(Transform: transform)])]);
        var host = TransformFixture.Host(context: context, section: section);
        var arena = host.Arena;
        var mask = TransformFixture.Ordinal(context: context, name: "mask");
        var slot = arena.Keys.Intern(name: StateRow.SlotKey);
        var before = arena.Read(rowOrdinal: mask, key: slot);
        Assert.True(arena.TryWriteVisibility(rowOrdinal: TransformFixture.Ordinal(context: context, name: liveEnd ? "rankValue" : "pointer"), key: slot, visibility: Private));
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);

        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal(before, arena.Read(rowOrdinal: mask, key: slot));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
        Assert.Equal("", TransformFixture.Listing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "hand")));
        Assert.Equal("0,0,0,0", TransformFixture.BoardListing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "target")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LiveRestrictionsAreCheckedBeforeAnyTransformWrites(bool zone, bool confined) {
        var section = confined ? WithPolicies("deck") : WithPolicies();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(RuleCompiler.TryResolveTransform(new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]), context, out var transform, out var reason), reason);
        var deck = TransformFixture.Ordinal(context: context, name: "deck");
        var restricted = TransformFixture.Ordinal(context: context, name: zone ? "deck" : "rank");
        Assert.True(host.Arena.TryWriteVisibility(rowOrdinal: restricted, key: host.Arena.Keys.Intern(name: TransformFixture.Name(value: "a")), visibility: Private));
        var before = TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck);

        var applied = host.TryTransform(binding: ArenaTransformBinding.None, transform: transform, moved: out var moved, refusal: out var refusal);

        Assert.Equal(confined, applied);
        Assert.Equal(confined, moved);
        if (confined) {
            Assert.Equal("b=2,c=3,a=1", TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
        } else {
            Assert.Contains("TransformWidensAudience", refusal.Reason);
            Assert.Equal(before, TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenRuleLocalsCannotControlAPublicTransform(bool runtime) {
        var section = runtime ? WithPolicies() : WithPolicies("rankValue");
        var context = TransformFixture.Context(section: section);
        Assert.True(ExpressionSpelling.TryParse(text: "1 / (rankValue + 1)", program: out var program, error: out var error), error);
        var rule = new Rule(
            Name: TransformFixture.Name(value: "localControl"),
            Mode: ActionTriggerMode.Level,
            Locals: [new RuleLocal(Name: TransformFixture.Name(value: "probe"), Expression: program!)],
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]))]);
        if (!runtime) {
            var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
            Assert.Contains("TransformWidensAudience", exception.Message);
            return;
        }
        var rules = RuleCompiler.CompileAll(context: context, rules: [rule]);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(host.Arena.TryWriteVisibility(
            rowOrdinal: TransformFixture.Ordinal(context: context, name: "rankValue"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey),
            visibility: Private));
        var evaluator = new RuleEvaluator(host: host);
        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);
        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
    }
}
