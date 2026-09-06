using Xunit;

namespace Puck.State.Tests;

/// <summary>Ordered pile endpoints preserve string identity and follow the active transaction frame.</summary>
public sealed class ZoneEndKeyLawTests {
    private static CellName Name(string value) => CellName.Parse(value);
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows => rows;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
    }
    private static (FrameHost Host, RuleCompileContext Context, StateRow[] Rows) Build() {
        StateRow[] rows = [
            new(Name("cards"), CellKind.Int, Capacity: 3, Cells: [new(Name("alpha"), 5), new(Name("beta"), 6), new(Name("gamma"), 7)]),
            new(Name("deck"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: [new(Name("gamma"), 1), new(Name("alpha"), 1), new(Name("beta"), 1)]),
            new(Name("hand"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: []),
        ];
        var section = new Section(rows);
        var catalog = StateCatalog.Compile(section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);
        var host = new FrameHost(new FrameLayout(rows, _ => null), rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));
        return (host, context, rows);
    }
    private static CompiledCellRef Key(RuleCompileContext context, string spelling) {
        Assert.True(RuleCompiler.TryResolveDynamicKey(spelling, "end-law", context, "transfer", "key", out var key));
        return key;
    }
    private static string Resolve(FrameHost host, CompiledCellRef key) => RuleEvaluation.ResolveKey(host, null, key);

    [Fact]
    public void EndpointKeysUsePileOrderAndSeeUncommittedFrameTransfers() {
        var (host, context, rows) = Build();
        var first = Key(context, "$zone:deck:first");
        var last = Key(context, "$zone:deck:last");
        Assert.Equal("gamma", Resolve(host, first));
        Assert.Equal("beta", Resolve(host, last));
        Assert.Equal("", Resolve(host, Key(context, "$zone:hand:last")));
        Assert.True(host.Frame.TryTransfer(new StateTransform.Transfer("deck", "hand", ZoneSelector.First), out var reason), reason);
        Assert.Equal("alpha", Resolve(host, first));
        Assert.Equal("gamma", Resolve(host, Key(context, "$zone:hand:last")));
        Assert.Equal(3, rows[1].Cells!.Count);
        List<RuleAccess> reads = [];
        first.Custom!.CollectReads(reads);
        Assert.Equal([new RuleAccess("deck", null)], reads);
        Assert.False(first.Custom.HostOnly);
        context.BindingScope = [BoundKey.Token, BoundKey.Previous];
        var expression = RuleCompiler.CompileExpression(ValueExpression.Parse("cards[$token]"), CellKind.Int, "end-law", "value", context);
        Span<long> word = stackalloc long[3];
        var length = PatternOperand.ReadTupleWord(host, rows[1], expression, CellKind.Int, word);
        Assert.Equal(new long[] { 5, 6 }, word[..length].ToArray());
        Assert.Null(host.BoundTokenKey);
        Assert.Null(host.BoundPreviousKey);
    }

    [Theory]
    [InlineData("Equal")]
    [InlineData("NotEqual")]
    [InlineData("LessOrEqual")]
    public void AnEmptyZoneEndpointHoldsNoComparisonAndCopiesNothing(string comparison) {
        var (host, context, rows) = Build();
        var gate = new ActionPredicate.CompareState("cards", Enum.Parse<ActionStateComparison>(comparison), 0, Key: "$zone:hand:last");
        var rules = RuleCompiler.CompileAll([
            new Rule(Name("gated"), [new ActionEffect.SetState("cards", Value: 9, Key: "beta")], Gate: gate),
            new Rule(Name("copy"), [new ActionEffect.SetState("cards", Key: "beta", FromState: "cards", FromKey: "$zone:hand:last")]),
        ], context);
        Assert.False(host.Judge(rules, 1));
        Assert.True(host.Frame.TryStored(rows[0], Name("beta"), out var beta, out _));
        Assert.Equal(6, beta);
        Assert.True(host.Frame.TryTransfer(new StateTransform.Transfer("deck", "hand", ZoneSelector.First), out var reason), reason);
        Assert.Equal(comparison != "NotEqual" ? false : true, host.Judge([rules[0]], 2));
        Assert.True(host.Judge([rules[1]], 3));
        Assert.True(host.Frame.TryStored(rows[0], Name("beta"), out beta, out _));
        Assert.Equal(7, beta);
    }

    [Theory]
    [InlineData("$zone:cards:first")]
    [InlineData("$zone:missing:first")]
    [InlineData("$zone:deck:middle")]
    [InlineData("$zone:deck")]
    [InlineData("$zone:deck:first:extra")]
    public void OnlyEndpointsOfDeclaredOrderedZonesCompile(string spelling) {
        var (_, context, _) = Build();
        Assert.Throws<RuleException>(() => Key(context, spelling));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicTransfersResolveEachStepAndRollBackTogether(bool fail) {
        var (host, context, _) = Build();
        List<ActionEffect> steps = [
            new ActionEffect.TransformState(new StateTransform.Transfer("deck", "hand", ZoneSelector.Key, "$zone:deck:first")),
            new ActionEffect.TransformState(new StateTransform.Transfer("deck", "hand", ZoneSelector.Key, "$zone:deck:first")),
        ];
        if (fail) { steps.Add(new ActionEffect.TransformState(new StateTransform.Transfer("deck", "hand", ZoneSelector.Key, "gamma"))); }
        var rules = RuleCompiler.CompileAll([new Rule(Name("move"), [new ActionEffect.Transaction(steps)])], context);
        Assert.Equal(!fail, host.Judge(rules, 1));
        Assert.Equal(fail ? "gamma" : "beta", Resolve(host, Key(context, "$zone:deck:first")));
        Assert.Equal(fail ? "" : "alpha", Resolve(host, Key(context, "$zone:hand:last")));
        Assert.Contains(new RuleAccess("deck", null), RuleDataflow.Reads(rules[0]));
    }

    [Theory]
    [InlineData("$zones[cards[beta]]", "hand", null)]
    [InlineData("deck", "$zones[cards[beta]]", new[] { "cards" })]
    [InlineData("$zones[cards[beta]]", "$zones[cards[beta]]", new[] { "deck", "hand", "deck" })]
    public void LiveZoneEndsNeedAWellFormedZoneTable(string from, string to, string[]? zones) {
        var (_, context, _) = Build();
        var transfer = new StateTransform.Transfer(from, to, ZoneSelector.First);
        Assert.Throws<RuleException>(() => RuleCompiler.CompileAll([new Rule(Name("move"), [new ActionEffect.TransformState(transfer)], Zones: zones)], context));
    }

    [Fact]
    public void PatternWorkUsesDeclaredCapacityAndOpcodeCostsAndTracksAllSources() {
        var (_, context, _) = Build();
        var expression = RuleCompiler.CompileExpression(ValueExpression.Parse("cards[alpha] % 13"), CellKind.Int, "end-law", "value", context);
        var operand = new PatternOperand("deck", null, null, default, "pattern", null, "cards", default, MatchFacet.Prefix, expression);
        // Three tokens, each with one match step and (read + constant + integer modulo).
        Assert.Equal(3 * (1 + 1 + 1 + 16), operand.Cost(context));
        List<RuleAccess> reads = [];
        operand.CollectReads(reads);
        Assert.Contains(new RuleAccess("deck", null), reads);
        Assert.Contains(new RuleAccess("cards", null), reads);
        Assert.Contains(new RuleAccess("cards", "alpha"), reads);
        var expensive = new PatternOperand("deck", null, null, default, "pattern", null, null, default, MatchFacet.Prefix,
            [new CompiledExpressionToken(ExpressionOp.Operand, Operand: new MaxCostOperand())]);
        Assert.Equal(long.MaxValue, expensive.Cost(context));
    }
    private sealed class MaxCostOperand() : OperandFact(CellKind.Int) {
        public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(0, CellKind.Int);
        public override long Cost(RuleCompileContext context) => long.MaxValue;
    }
}
