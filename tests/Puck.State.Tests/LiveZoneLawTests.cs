using Xunit;

namespace Puck.State.Tests;

/// <summary>A rule's <c>zones</c> table and the <c>$zones[&lt;index&gt;]</c> row position: one rule reads, matches, and
/// moves between whichever zones its live indices select, and an index selecting none is the absent fact.</summary>
public sealed class LiveZoneLawTests {
    private static CellName Name(string value) => CellName.Parse(value);
    private static readonly string[] s_zones = ["deck", "hand", ""];
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows => rows;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
    }
    // cards: alpha=5, beta=6, gamma=7; deck holds [gamma, alpha, beta]; hand is empty; face is an attribute row over
    // the same cards. beta and gamma double as the live indices the rules read.
    private static (FrameHost Host, RuleCompileContext Context, StateRow[] Rows) Build() {
        StateRow[] rows = [
            new(Name("cards"), CellKind.Int, Capacity: 3, Cells: [new(Name("alpha"), 5), new(Name("beta"), 6), new(Name("gamma"), 7)]),
            new(Name("face"), CellKind.Int, Capacity: 3, Domain: new StateDomain.KeysOf(Name("cards")), Cells: [new(Name("alpha"), 1), new(Name("beta"), 0), new(Name("gamma"), 1)]),
            new(Name("deck"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: [new(Name("gamma"), 1), new(Name("alpha"), 1), new(Name("beta"), 1)]),
            new(Name("hand"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Cells: []),
            new(Name("other"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(Name("face"), Ordered: true), Cells: []),
        ];
        var section = new Section(rows);
        var catalog = StateCatalog.Compile(section);
        var faces = new PatternRow(Name("faces"), CellKind.Int, [new PatternSymbol(Name("up"), 1m, 1m)], new PatternNode.Star(new PatternNode.Symbol("up")), Attribute: "face");
        var context = new RuleCompileContext(section, catalog, null, [faces], null, 240, RuleVocabulary.Core);
        List<string> errors = [];
        Assert.True(CompiledPatterns.TryCompileAll([faces], out var patterns, errors), string.Join("; ", errors));
        var host = new FrameHost(new FrameLayout(rows, _ => null), rows, catalog, patterns, []);
        host.Frame.Load(new RowStore(rows));
        return (host, context, rows);
    }
    private static Rule R(string name, ActionPredicate? gate, params ActionEffect[] effects) => new(Name(name), effects, Gate: gate, Zones: s_zones);
    private static ActionPredicate.CompareState Cmp(string state, ActionStateComparison comparison, decimal value, string? key = null) => new(state, comparison, value, Key: key);
    private static void Seed(FrameHost host, string key, long index) {
        Assert.True(host.TryApply(new StateMutation.UpsertCell("cards", key, index, StateWriteKind.Set), 1, false, out var reason), reason);
    }
    private static long Stored(FrameHost host, StateRow[] rows, string row, string key) {
        Assert.True(host.Frame.TryStored(rows.Single(r => r.Name == row), Name(key), out var value, out _));
        return value;
    }

    [Theory]
    [InlineData(0, true, 3, "gamma", 7)]
    [InlineData(1, true, 0, "", 0)]
    [InlineData(2, false, 0, "", 0)]
    [InlineData(-1, false, 0, "", 0)]
    [InlineData(9, false, 0, "", 0)]
    public void ALiveIndexSelectsItsZoneForEveryReadAndNoneClosesTheGate(long index, bool selects, long count, string first, long firstValue) {
        var (host, context, rows) = Build();
        var rules = RuleCompiler.CompileAll([
            // The zone's count, its first member's key, and that member's card value, each through the live zone.
            R("count", Cmp("$reduce:count:$zones[cards[beta]]", ActionStateComparison.GreaterOrEqual, 0), new ActionEffect.SetState("cards", Key: "alpha", Expression: ValueExpression.Parse("$reduce:count:$zones[cards[beta]]"))),
            R("first", Cmp("cards", ActionStateComparison.GreaterOrEqual, 0, key: "$zone:$zones[cards[beta]]:first"), new ActionEffect.SetState("cards", Key: "alpha", FromState: "cards", FromKey: "$zone:$zones[cards[beta]]:first")),
            R("member", Cmp("$zones[cards[beta]]", ActionStateComparison.Equal, 1, key: "gamma"), new ActionEffect.SetState("cards", Key: "alpha", Value: 1)),
        ], context);
        Seed(host, "beta", index);
        // An empty selected zone still counts (zero); an unselected index holds no comparison at all.
        Assert.Equal(selects, host.Judge([rules[0]], 2));
        Assert.Equal(selects ? count : 5, Stored(host, rows, "cards", "alpha"));
        Assert.Equal(first.Length != 0, host.Judge([rules[1]], 3));
        Assert.Equal(first.Length != 0 ? firstValue : (selects ? count : 5), Stored(host, rows, "cards", "alpha"));
        Assert.Equal(index == 0, host.Judge([rules[2]], 4));
        var reads = RuleDataflow.Reads(rules[2]);
        Assert.Contains(new RuleAccess("deck", null), reads);
        Assert.Contains(new RuleAccess("hand", null), reads);
        Assert.Contains(new RuleAccess("cards", "beta"), reads);
        Assert.DoesNotContain(new RuleAccess("other", null), reads);
        Assert.False(RuleDataflow.ReadsHost(rules[2]));
        Assert.Empty(host.Evaluator.Diagnostics());
        // Read outside an evaluation, an unselected reference is the absent fact.
        var operand = rules[2].Gate[0].Left!;
        Assert.Equal(!selects, operand.Read(host).IsAbsent);
    }

    [Fact]
    public void ALiveZoneWordMatchesAndAnAbsentZoneRefusesTheExpression() {
        var (host, context, rows) = Build();
        var rules = RuleCompiler.CompileAll([
            R("faces", null, new ActionEffect.SetState("cards", Key: "alpha", Expression: ValueExpression.Parse("$match:faces:$zones[cards[beta]]:prefix + 10"))),
        ], context);
        Seed(host, "beta", 0);
        Assert.True(host.Judge(rules, 2));
        // deck reads face values [gamma=1, alpha=1, beta=0]: the accepted prefix is two cards long.
        Assert.Equal(12, Stored(host, rows, "cards", "alpha"));
        // An index at the table's gap is not an error: the evaluation is not for it, and the trace names the miss.
        Seed(host, "beta", 2);
        Assert.True(host.Evaluator.ArmTrace("faces", 1));
        Assert.False(host.Judge(rules, 3));
        Assert.Equal(12, Stored(host, rows, "cards", "alpha"));
        Assert.Empty(host.Evaluator.Diagnostics());
        var trace = host.Evaluator.DescribeTrace("trace")!;
        Assert.Contains("zones [$zones[cards[beta]] -> none] gate=closed: not for these zones", trace, StringComparison.Ordinal);
        Seed(host, "beta", 1);
        Assert.True(host.Evaluator.ArmTrace("faces", 1));
        Assert.True(host.Judge(rules, 4));
        Assert.Contains("zones [$zones[cards[beta]] -> hand] gate=open", host.Evaluator.DescribeTrace("trace")!, StringComparison.Ordinal);
        Assert.Single(rules[0].Zones!.References);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 2, false)]
    [InlineData(0, 7, false)]
    public void ALiveTransferMovesBetweenTheZonesItsIndicesSelect(long from, long to, bool moves) {
        var (host, context, _) = Build();
        var transfer = new StateTransform.Transfer("$zones[cards[beta]]", "$zones[cards[gamma]]", ZoneSelector.First);
        var rules = RuleCompiler.CompileAll([R("move", null, new ActionEffect.TransformState(transfer))], context);
        var effect = Assert.IsType<TransformStateEffect>(rules[0].Effects[0]);
        Assert.NotNull(effect.FromZone);
        Assert.NotNull(effect.ToZone);
        Assert.Equal(["deck", "hand"], RuleDataflow.Writes(rules[0]).Select(access => access.Row).Distinct().Order());
        Assert.Equal(["cards"], RuleDataflow.Reads(rules[0]).Select(access => access.Row).Distinct());
        Seed(host, "beta", from);
        Seed(host, "gamma", to);
        Assert.Equal(moves, host.Judge(rules, 2));
        Assert.True(RuleCompiler.TryResolveDynamicKey("$zone:hand:last", "law", context, "transfer", "key", out var last));
        Assert.Equal(moves ? "gamma" : "", RuleEvaluation.ResolveKey(host, null, last));
    }

    [Fact]
    public void ForEachOverTheTableVisitsEveryNonEmptyIndexAsEach() {
        var (host, context, rows) = Build();
        var rule = new Rule(Name("tally"), [new ActionEffect.AddState("cards", Key: "alpha", Expression: ValueExpression.Parse("$reduce:count:$zones[$each] + 1"))], ForEach: "$zones", Zones: s_zones);
        var rules = RuleCompiler.CompileAll([rule], context);
        Assert.Equal(["0", "1"], rules[0].Zones!.Indices.Select(index => index.Value));
        Assert.True(host.Judge(rules, 2));
        // deck (index 0) holds three cards, hand (index 1) none: 5 + (3 + 1) + (0 + 1).
        Assert.Equal(10, Stored(host, rows, "cards", "alpha"));
    }

    [Fact]
    public void ABindingIndexesTheTableAndIsAKeyAnywhere() {
        var (host, context, rows) = Build();
        var rule = new Rule(
            Name("bound"),
            [new ActionEffect.SetState("cards", Key: "alpha", FromState: "cards", FromKey: "$zone:$zones[$bind:pile]:last")],
            Bindings: [new RuleBinding(Name("pile"), CellKind.Int, ValueExpression.Parse("cards[beta] - 6"))],
            Zones: s_zones
        );
        var rules = RuleCompiler.CompileAll([rule], context);
        Assert.True(host.Judge(rules, 2));
        Assert.Equal(6, Stored(host, rows, "cards", "alpha"));
    }

    // The lexer keeps the bracketed index verbatim inside the name; the live-zone compiler parses it as a key.
    [Theory]
    [InlineData("$reduce:count:$zones[cards[beta]] + 1", "$reduce:count:$zones[cards[beta]]")]
    [InlineData("$match:faces:$zones[cards[beta] + 1]:prefix", "$match:faces:$zones[cards[beta] + 1]:prefix")]
    [InlineData("$zones[$each][gamma]", "$zones[$each]")]
    public void TheInfixSpellingKeepsALiveZoneIndexInsideTheName(string text, string name) {
        Assert.True(ExpressionSpelling.TryParse(text, out var tokens, out var error), error);
        var read = Assert.IsType<ValueToken.State>(tokens[0]);
        Assert.Equal(name, read.Name);
        Assert.Equal(text, ExpressionSpelling.Print(tokens));
        Assert.True(ExpressionSpelling.IsBareName(name));
    }

    [Theory]
    [InlineData("$zones[cards[beta]]", null, "member")]
    [InlineData("$zones[3]", null, "literal index")]
    [InlineData("$zones[cards[beta]", null, "unclosed")]
    [InlineData("$zones[$expr:cards[beta]]", null, "spelled expr")]
    [InlineData("$zones[cards[beta]]", new[] { "deck", "face" }, "not a zone")]
    [InlineData("$zones[cards[beta]]", new[] { "deck", "other" }, "another domain")]
    [InlineData("$zones[cards[beta]]", new[] { "deck", "deck" }, "twice")]
    [InlineData("$zones[cards[beta]]", new string[0], "empty")]
    [InlineData("$zones[cards[beta]]", new[] { "" }, "all gaps")]
    [InlineData("$symmetry:ring:$zones[cards[beta]]", new[] { "deck" }, "symmetry")]
    public void MalformedTablesAndSpellingsRefuse(string state, string[]? zones, string why) {
        var (_, context, _) = Build();
        var rule = new Rule(Name("bad"), [new ActionEffect.SetState("cards", Key: "alpha", Value: 1)], Gate: Cmp(state, ActionStateComparison.Equal, 1, key: "gamma"), Zones: zones);
        var refusal = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll([rule], context));
        Assert.NotEmpty(why);
        Assert.NotEmpty(refusal.Message);
    }

    [Fact]
    public void ForEachOverZonesNeedsATableAndATransferEndAgreesWithTheTableDomain() {
        var (_, context, _) = Build();
        Assert.Throws<RuleException>(() => RuleCompiler.CompileAll([new Rule(Name("sweep"), [new ActionEffect.SetState("cards", Key: "alpha", Value: 1)], ForEach: "$zones")], context));
        var crossed = new StateTransform.Transfer("$zones[cards[beta]]", "other", ZoneSelector.First);
        Assert.Throws<RuleException>(() => RuleCompiler.CompileAll([R("cross", null, new ActionEffect.TransformState(crossed))], context));
    }
}
