using Xunit;

namespace Puck.State.Tests;

/// <summary>A rule's <c>zones</c> table and the <c>$zones[&lt;index&gt;]</c> row position: one rule reads, matches, and
/// moves between whichever zones its live indices select, and an index selecting none is the absent fact.</summary>
public sealed class LiveZoneLawTests {
    // cards: alpha=5, beta=6, gamma=7; deck holds [gamma, alpha, beta]; hand is empty; face is an attribute row over
    // the same cards. beta and gamma double as the live indices the rules read.
    private static (FrameHost Host, RuleCompileContext Context, StateRow[] Rows) Build() {
        StateRow[] rows = [
            new(
                Name(value: "cards"),
                CellKind.Int,
                Capacity: 3,
                Cells: [new(
                        Name(value: "alpha"),
                        5
                    ), new(
                        Name(value: "beta"),
                        6
                    ), new(
                        Name(value: "gamma"),
                        7
                    )]
            ),
            new(
                Name(value: "face"),
                CellKind.Int,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(Name(value: "cards")),
                Cells: [new(
                        Name(value: "alpha"),
                        1
                    ), new(
                        Name(value: "beta"),
                        0
                    ), new(
                        Name(value: "gamma"),
                        1
                    )]
            ),
            new(
                Name(value: "deck"),
                CellKind.Bool,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(
                    Name(value: "cards"),
                    Ordered: true
                ),
                Cells: [new(
                        Name(value: "gamma"),
                        1
                    ), new(
                        Name(value: "alpha"),
                        1
                    ), new(
                        Name(value: "beta"),
                        1
                    )]
            ),
            new(
                Name(value: "hand"),
                CellKind.Bool,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(
                    Name(value: "cards"),
                    Ordered: true
                ),
                Cells: []
            ),
            new(
                Name(value: "other"),
                CellKind.Bool,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(
                    Name(value: "face"),
                    Ordered: true
                ),
                Cells: []
            ),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var faces = new PatternRow(
            Name(value: "faces"),
            CellKind.Int,
            [new PatternSymbol(
                    Name(value: "up"),
                    1m,
                    1m
                )],
            new PatternNode.Star(Item: new PatternNode.Symbol(Name: "up")),
            Attribute: "face"
        );
        var context = new RuleCompileContext(
            section,
            catalog,
            null,
            [faces],
            null,
            240,
            RuleVocabulary.Core
        );
        List<string> errors = [];

        Assert.True(
            condition: CompiledPatterns.TryCompileAll(
                errors: errors,
                patterns: out var patterns,
                rows: [faces]
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );
        var host = new FrameHost(
            new FrameLayout(
                rows: rows,
                topology: _ => null
            ),
            rows,
            catalog,
            patterns,
            []
        );

        host.Frame.Load(source: new RowStore(rows: rows));
        return (host, context, rows);
    }
    private static ActionPredicate.CompareState Cmp(string state, ActionStateComparison comparison, decimal value, string? key = null) => new(
        state,
        comparison,
        value,
        Key: key
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static Rule R(string name, ActionPredicate? gate, params ActionEffect[] effects) => new(
        Name(value: name),
        effects,
        Gate: gate,
        Zones: Zones
    );
    private static void Seed(FrameHost host, string key, long index) {
        Assert.True(
            condition: host.TryApply(
                new StateMutation.UpsertCell(
                    "cards",
                    key,
                    index,
                    StateWriteKind.Set
                ),
                1,
                false,
                out var reason
            ),
            userMessage: reason
        );
    }
    private static long Stored(FrameHost host, StateRow[] rows, string row, string key) {
        Assert.True(condition: host.Frame.TryStored(
            rows.Single(predicate: r => (r.Name == row)),
            Name(value: key),
            out var value,
            out _
        ));
        return value;
    }

    [Fact]
    public void ABindingIndexesTheTableAndIsAKeyAnywhere() {
        var (host, context, rows) = Build();
        var rule = new Rule(
            Name(value: "bound"),
            [new ActionEffect.SetState(
                    "cards",
                    Key: "alpha",
                    FromState: "cards",
                    FromKey: "$zone:$zones[$bind:pile]:last"
                )],
            Bindings: [new RuleBinding(
                    Name(value: "pile"),
                    CellKind.Int,
                    ValueExpression.Parse(text: "cards[beta] - 6")
                )],
            Zones: Zones
        );
        var rules = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );

        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 2,
            engineTick: 2
        ));
        Assert.Equal(
            6,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
    }
    [InlineData(0, true, 3, "gamma", 7)]
    [InlineData(1, true, 0, "", 0)]
    [InlineData(2, false, 0, "", 0)]
    [InlineData(-1, false, 0, "", 0)]
    [InlineData(9, false, 0, "", 0)]
    [Theory]
    public void ALiveIndexSelectsItsZoneForEveryReadAndNoneClosesTheGate(long index, bool selects, long count, string first, long firstValue) {
        var (host, context, rows) = Build();
        var rules = RuleCompiler.CompileAll(
            [
            // The zone's count, its first member's key, and that member's card value, each through the live zone.
            R(
                    "count",
                    Cmp(
                        "$reduce:count:$zones[cards[beta]]",
                        ActionStateComparison.GreaterOrEqual,
                        0
                    ),
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        Expression: ValueExpression.Parse(text: "$reduce:count:$zones[cards[beta]]")
                    )
                ),
            R(
                    "first",
                    Cmp(
                        "cards",
                        ActionStateComparison.GreaterOrEqual,
                        0,
                        key: "$zone:$zones[cards[beta]]:first"
                    ),
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        FromState: "cards",
                        FromKey: "$zone:$zones[cards[beta]]:first"
                    )
                ),
            R(
                    "member",
                    Cmp(
                        "$zones[cards[beta]]",
                        ActionStateComparison.Equal,
                        1,
                        key: "gamma"
                    ),
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        Value: 1
                    )
                ),
        ],
            context
        );

        Seed(
            host: host,
            index: index,
            key: "beta"
        );
        // An empty selected zone still counts (zero); an unselected index holds no comparison at all.
        Assert.Equal(
            selects,
            host.Judge(
                rules: [rules[0]],
                tick: 2,
                engineTick: 2
            )
        );
        Assert.Equal(
            (selects
            ? count
            : 5),
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        Assert.Equal(
            (first.Length != 0),
            host.Judge(
                rules: [rules[1]],
                tick: 3,
                engineTick: 3
            )
        );
        Assert.Equal(
            ((first.Length != 0)
            ? firstValue
            : (selects
                ? count
                : 5)),
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        Assert.Equal(
            (index == 0),
            host.Judge(
                rules: [rules[2]],
                tick: 4,
                engineTick: 4
            )
        );
        var reads = RuleDataflow.Reads(rule: rules[2]);

        Assert.Contains(
            new RuleAccess(
                "deck",
                null
            ),
            reads
        );
        Assert.Contains(
            new RuleAccess(
                "hand",
                null
            ),
            reads
        );
        Assert.Contains(
            new RuleAccess(
                "cards",
                "beta"
            ),
            reads
        );
        Assert.DoesNotContain(
            new RuleAccess(
                "other",
                null
            ),
            reads
        );
        Assert.False(condition: RuleDataflow.ReadsHost(rule: rules[2]));
        Assert.Empty(collection: host.Evaluator.Diagnostics());
        // Read outside an evaluation, an unselected reference is the absent fact.
        var operand = rules[2].Gate[0].Left!;

        Assert.Equal(
            !selects,
            operand.Read(reader: host).IsAbsent
        );
    }
    [InlineData(0, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 2, false)]
    [InlineData(0, 7, false)]
    [Theory]
    public void ALiveTransferMovesBetweenTheZonesItsIndicesSelect(long from, long to, bool moves) {
        var (host, context, _) = Build();
        var transfer = new StateTransform.Transfer(
            "$zones[cards[beta]]",
            "$zones[cards[gamma]]",
            ZoneSelector.First
        );
        var rules = RuleCompiler.CompileAll(
            [R(
                    "move",
                    null,
                    new ActionEffect.TransformState(Transform: transfer)
                )],
            context
        );
        var effect = Assert.IsType<TransformStateEffect>(@object: rules[0].Effects[0]);

        Assert.NotNull(@object: effect.FromZone);
        Assert.NotNull(@object: effect.ToZone);
        Assert.Equal(
            ["deck", "hand"],
            RuleDataflow.Writes(rule: rules[0]).Select(selector: access => access.Row).Distinct().Order()
        );
        Assert.Equal(
            ["cards"],
            RuleDataflow.Reads(rule: rules[0]).Select(selector: access => access.Row).Distinct()
        );
        Seed(
            host: host,
            index: from,
            key: "beta"
        );
        Seed(
            host: host,
            index: to,
            key: "gamma"
        );
        Assert.Equal(
            moves,
            host.Judge(
                rules: rules,
                tick: 2,
                engineTick: 2
            )
        );
        Assert.True(condition: RuleCompiler.TryResolveDynamicKey(
            cell: out var last,
            context: context,
            key: "$zone:hand:last",
            keyFieldLabel: "key",
            ruleName: "law",
            verb: "transfer"
        ));
        Assert.Equal(
            (moves
            ? "gamma"
            : ""),
            RuleEvaluation.ResolveKey(
                key: null,
                keyFrom: last,
                reader: host
            )
        );
    }
    [Fact]
    public void ALiveZoneWordMatchesAndAnAbsentZoneRefusesTheExpression() {
        var (host, context, rows) = Build();
        var rules = RuleCompiler.CompileAll(
            [
            R(
                    "faces",
                    null,
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        Expression: ValueExpression.Parse(text: "$match:faces:$zones[cards[beta]]:prefix + 10")
                    )
                ),
        ],
            context
        );

        Seed(
            host: host,
            index: 0,
            key: "beta"
        );
        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 2,
            engineTick: 2
        ));
        // deck reads face values [gamma=1, alpha=1, beta=0]: the accepted prefix is two cards long.
        Assert.Equal(
            12,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        // An index at the table's gap is not an error: the evaluation is not for it, and the trace names the miss.
        Seed(
            host: host,
            index: 2,
            key: "beta"
        );
        Assert.True(condition: host.Evaluator.ArmTrace(
            evaluations: 1,
            rule: "faces"
        ));
        Assert.False(condition: host.Judge(
            rules: rules,
            tick: 3,
            engineTick: 3
        ));
        Assert.Equal(
            12,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        Assert.Empty(collection: host.Evaluator.Diagnostics());
        var trace = host.Evaluator.DescribeTrace(verb: "trace")!;

        Assert.Contains(
            actualString: trace,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "zones [$zones[cards[beta]] -> none] gate=closed: not for these zones"
        );
        Seed(
            host: host,
            index: 1,
            key: "beta"
        );
        Assert.True(condition: host.Evaluator.ArmTrace(
            evaluations: 1,
            rule: "faces"
        ));
        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 4,
            engineTick: 4
        ));
        Assert.Contains(
            "zones [$zones[cards[beta]] -> hand] gate=open",
            host.Evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
        Assert.Single(collection: rules[0].Zones!.References);
    }
    [Fact]
    public void ASharedKeyCannotMakeARuleBindingVisibleInsideAPattern() {
        var (_, context, _) = Build();
        RuleCompiler.BeginScope(
            R(
                "scope",
                null,
                new ActionEffect.SetState(
                    "cards",
                    Key: "alpha",
                    Value: 1
                )
            ) with { ForEach = "cards" },
            context
        );
        _ = RuleCompiler.CompileKeyExpression(
            context: context,
            ruleName: "scope",
            text: "cards[$each] + 0",
            where: "key"
        );
        var pattern = new PatternRow(
            Name(value: "bad"),
            CellKind.Int,
            [new PatternSymbol(
                    Name(value: "up"),
                    1,
                    1
                )],
            new PatternNode.Symbol(Name: "up"),
            Value: ValueExpression.Parse(text: "cards[cards[$each] + 0]")
        );

        Assert.False(condition: RuleCompiler.TryCompilePatternValue(
            context: context,
            pattern: pattern,
            reason: out _,
            ruleName: "scope",
            tokenDomain: "cards",
            tokens: out _
        ));
        context.ClearScope();
    }
    [Fact]
    public void DistinctExpressionKeysStillRespectTheBindingCeiling() {
        var (_, context, _) = Build();
        var gate = new ActionPredicate.All(Predicates: [.. Enumerable.Range(
                count: 17,
                start: 0
            ).Select(selector: index =>
            Cmp(
                $"$reduce:count:$zones[cards[beta] + {index}]",
                ActionStateComparison.GreaterOrEqual,
                0
            ))]);

        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            [R(
                    "distinct",
                    gate,
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        Value: 1
                    )
                )],
            context
        ));
    }
    [Fact]
    public void ForEachOverTheTableVisitsEveryNonEmptyIndexAsEach() {
        var (host, context, rows) = Build();
        var rule = new Rule(
            Name(value: "tally"),
            [new ActionEffect.AddState(
                    "cards",
                    Key: "alpha",
                    Expression: ValueExpression.Parse(text: "$reduce:count:$zones[$each] + 1")
                )],
            ForEach: "$zones",
            Zones: Zones
        );
        var rules = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );

        Assert.Equal(
            ["0", "1"],
            rules[0].Zones!.Indices.Select(selector: index => index.Value)
        );
        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 2,
            engineTick: 2
        ));
        // deck (index 0) holds three cards, hand (index 1) none: 5 + (3 + 1) + (0 + 1).
        Assert.Equal(
            10,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
    }
    [Fact]
    public void ForEachOverZonesNeedsATableAndATransferEndAgreesWithTheTableDomain() {
        var (_, context, _) = Build();
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            [new Rule(
                    Name(value: "sweep"),
                    [new ActionEffect.SetState(
                            "cards",
                            Key: "alpha",
                            Value: 1
                        )],
                    ForEach: "$zones"
                )],
            context
        ));
        var crossed = new StateTransform.Transfer(
            "$zones[cards[beta]]",
            "other",
            ZoneSelector.First
        );

        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            [R(
                    "cross",
                    null,
                    new ActionEffect.TransformState(Transform: crossed)
                )],
            context
        ));
    }
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
    [Theory]
    public void MalformedTablesAndSpellingsRefuse(string state, string[]? zones, string why) {
        var (_, context, _) = Build();
        var rule = new Rule(
            Name(value: "bad"),
            [new ActionEffect.SetState(
                    "cards",
                    Key: "alpha",
                    Value: 1
                )],
            Gate: Cmp(
                state,
                ActionStateComparison.Equal,
                1,
                key: "gamma"
            ),
            Zones: zones
        );
        var refusal = Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        ));

        Assert.NotEmpty(collection: why);
        Assert.NotEmpty(collection: refusal.Message);
    }
    [Fact]
    public void NestedKeyDependenciesCannotOverrunTheBindingCeiling() {
        var (_, context, _) = Build();
        var rule = R(
            "nested",
            null,
            new ActionEffect.SetState(
                "cards",
                Key: "alpha",
                Expression: ValueExpression.Parse(text: "cards[cards[cards[beta] + 0] + 0]")
            )
        ) with {
            Bindings = [.. Enumerable.Range(
                count: 15,
                start: 0
            ).Select(selector: index => new RuleBinding(
                Name(value: $"b{index}"),
                CellKind.Int,
                ValueExpression.Parse(text: "0")
            ))],
        };

        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        ));
    }
    [Fact]
    public void RepeatedExpressionKeysShareCapacityAndRefreshForEachEvaluation() {
        var (host, context, rows) = Build();
        var repeated = new ActionPredicate.All(Predicates: [.. Enumerable.Range(
                count: 17,
                start: 0
            ).Select(selector: _ =>
            Cmp(
                "$reduce:count:$zones[cards[beta] + 0]",
                ActionStateComparison.GreaterOrEqual,
                0
            ))]);
        var rules = RuleCompiler.CompileAll(
            [
            R(
                    "repeat",
                    repeated,
                    new ActionEffect.SetState(
                        "cards",
                        Key: "alpha",
                        Expression: ValueExpression.Parse(text: "$reduce:count:$zones[cards[beta] + 0]")
                    )
                ),
            R(
                    "next",
                    null,
                    new ActionEffect.SetState(
                        "cards",
                        Key: "gamma",
                        Expression: ValueExpression.Parse(text: "$reduce:count:$zones[cards[beta] + 0]")
                    )
                ),
        ],
            context
        );

        Assert.All(
            rules,
            rule => Assert.Single(collection: rule.Bindings!)
        );
        Seed(
            host: host,
            index: 0,
            key: "beta"
        );
        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 2,
            engineTick: 2
        ));
        Assert.Equal(
            3,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        Assert.Equal(
            3,
            Stored(
                host: host,
                key: "gamma",
                row: "cards",
                rows: rows
            )
        );
        Seed(
            host: host,
            index: 1,
            key: "beta"
        );
        Assert.True(condition: host.Judge(
            rules: rules,
            tick: 3,
            engineTick: 3
        ));
        Assert.Equal(
            0,
            Stored(
                host: host,
                key: "alpha",
                row: "cards",
                rows: rows
            )
        );
        Assert.Equal(
            0,
            Stored(
                host: host,
                key: "gamma",
                row: "cards",
                rows: rows
            )
        );
    }
    // The lexer keeps the bracketed index verbatim inside the name; the live-zone compiler parses it as a key.
    [Theory]
    [InlineData("$reduce:count:$zones[cards[beta]] + 1", "$reduce:count:$zones[cards[beta]]")]
    [InlineData("$match:faces:$zones[cards[beta] + 1]:prefix", "$match:faces:$zones[cards[beta] + 1]:prefix")]
    [InlineData("$zones[$each][gamma]", "$zones[$each]")]
    public void TheInfixSpellingKeepsALiveZoneIndexInsideTheName(string text, string name) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        var read = Assert.IsType<ValueToken.State>(@object: tokens[0]);

        Assert.Equal(
            name,
            read.Name
        );
        Assert.Equal(
            text,
            ExpressionSpelling.Print(tokens: tokens)
        );
        Assert.True(condition: ExpressionSpelling.IsBareName(name: name));
    }

    private static readonly string[] Zones = ["deck", "hand", ""];

    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }
}
