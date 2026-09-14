using Xunit;

namespace Puck.State.Tests;

/// <summary>Ordered pile endpoints preserve string identity and follow the active transaction frame.</summary>
public sealed class ZoneEndKeyLawTests {
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
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(
            section,
            catalog,
            null,
            null,
            null,
            240,
            RuleVocabulary.Core
        );
        var host = new FrameHost(
            new FrameLayout(
                rows: rows,
                topology: _ => null
            ),
            rows,
            catalog,
            CompiledPatterns.Empty,
            []
        );

        host.Frame.Load(source: new RowStore(rows: rows));
        return (host, context, rows);
    }
    private static CompiledCellRef Key(RuleCompileContext context, string spelling) {
        Assert.True(condition: RuleCompiler.TryResolveDynamicKey(
            cell: out var key,
            context: context,
            key: spelling,
            keyFieldLabel: "key",
            ruleName: "end-law",
            verb: "transfer"
        ));
        return key;
    }
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static string Resolve(FrameHost host, CompiledCellRef key) => RuleEvaluation.ResolveKey(
        key: null,
        keyFrom: key,
        reader: host
    );

    [InlineData("Equal")]
    [InlineData("NotEqual")]
    [InlineData("LessOrEqual")]
    [Theory]
    public void AnEmptyZoneEndpointHoldsNoComparisonAndCopiesNothing(string comparison) {
        var (host, context, rows) = Build();
        var gate = new ActionPredicate.CompareState(
            "cards",
            Enum.Parse<ActionStateComparison>(value: comparison),
            0,
            Key: "$zone:hand:last"
        );
        var rules = RuleCompiler.CompileAll(
            [
            new Rule(
                    Name(value: "gated"),
                    [new ActionEffect.SetState(
                            "cards",
                            Value: 9,
                            Key: "beta"
                        )],
                    Gate: gate
                ),
            new Rule(
                    Name(value: "copy"),
                    [new ActionEffect.SetState(
                            "cards",
                            Key: "beta",
                            FromState: "cards",
                            FromKey: "$zone:hand:last"
                        )]
                ),
        ],
            context
        );

        Assert.False(condition: host.Judge(
            rules: rules,
            tick: 1,
            engineTick: 1
        ));
        Assert.True(condition: host.Frame.TryStored(
            rows[0],
            Name(value: "beta"),
            out var beta,
            out _
        ));
        Assert.Equal(
            actual: beta,
            expected: 6
        );
        Assert.True(
            condition: host.Frame.TryTransfer(
                new StateTransform.Transfer(
                    "deck",
                    "hand",
                    ZoneSelector.First
                ),
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            ((comparison != "NotEqual")
            ? false
            : true),
            host.Judge(
                rules: [rules[0]],
                tick: 2,
                engineTick: 2
            )
        );
        Assert.True(condition: host.Judge(
            rules: [rules[1]],
            tick: 3,
            engineTick: 3
        ));
        Assert.True(condition: host.Frame.TryStored(
            rows[0],
            Name(value: "beta"),
            out beta,
            out _
        ));
        Assert.Equal(
            actual: beta,
            expected: 7
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DynamicTransfersResolveEachStepAndRollBackTogether(bool fail) {
        var (host, context, _) = Build();
        List<ActionEffect> steps = [
            new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                "deck",
                "hand",
                ZoneSelector.Key,
                "$zone:deck:first"
            )),
            new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                "deck",
                "hand",
                ZoneSelector.Key,
                "$zone:deck:first"
            )),
        ];

        if (fail) { steps.Add(item: new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
            "deck",
            "hand",
            ZoneSelector.Key,
            "gamma"
        ))); }
        var rules = RuleCompiler.CompileAll(
            [new Rule(
                    Name(value: "move"),
                    [new ActionEffect.Transaction(steps)]
                )],
            context
        );

        Assert.Equal(
            !fail,
            host.Judge(
                rules: rules,
                tick: 1,
                engineTick: 1
            )
        );
        Assert.Equal(
            (fail
            ? "gamma"
            : "beta"),
            Resolve(
                host: host,
                key: Key(
                    context: context,
                    spelling: "$zone:deck:first"
                )
            )
        );
        Assert.Equal(
            (fail
            ? ""
            : "alpha"),
            Resolve(
                host: host,
                key: Key(
                    context: context,
                    spelling: "$zone:hand:last"
                )
            )
        );
        Assert.Contains(
            new RuleAccess(
                "deck",
                null
            ),
            RuleDataflow.Reads(rule: rules[0])
        );
    }
    [Fact]
    public void EndpointKeysUsePileOrderAndSeeUncommittedFrameTransfers() {
        var (host, context, rows) = Build();
        var first = Key(
            context: context,
            spelling: "$zone:deck:first"
        );
        var last = Key(
            context: context,
            spelling: "$zone:deck:last"
        );

        Assert.Equal(
            "gamma",
            Resolve(
                host: host,
                key: first
            )
        );
        Assert.Equal(
            "beta",
            Resolve(
                host: host,
                key: last
            )
        );
        Assert.Equal(
            "",
            Resolve(
                host: host,
                key: Key(
                    context: context,
                    spelling: "$zone:hand:last"
                )
            )
        );
        Assert.True(
            condition: host.Frame.TryTransfer(
                new StateTransform.Transfer(
                    "deck",
                    "hand",
                    ZoneSelector.First
                ),
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "alpha",
            Resolve(
                host: host,
                key: first
            )
        );
        Assert.Equal(
            "gamma",
            Resolve(
                host: host,
                key: Key(
                    context: context,
                    spelling: "$zone:hand:last"
                )
            )
        );
        Assert.Equal(
            3,
            rows[1].Cells!.Count
        );
        List<RuleAccess> reads = [];

        first.Custom!.CollectReads(into: reads);
        Assert.Equal(
            [new RuleAccess(
                    "deck",
                    null
                )],
            reads
        );
        Assert.False(condition: first.Custom.HostOnly);
        context.BindingScope = [BoundKey.Token, BoundKey.Previous];
        var expression = RuleCompiler.CompileExpression(
            ValueExpression.Parse(text: "cards[$token]"),
            CellKind.Int,
            "end-law",
            "value",
            context
        );
        Span<long> word = stackalloc long[3];
        var length = PatternOperand.ReadTupleWord(
            host,
            rows[1],
            expression,
            CellKind.Int,
            word
        );

        Assert.Equal(
            new long[] { 5, 6 },
            word[..length].ToArray()
        );
        Assert.Null(@object: host.BoundTokenKey);
        Assert.Null(@object: host.BoundPreviousKey);
    }
    [InlineData("$zones[cards[beta]]", "hand", null)]
    [InlineData("deck", "$zones[cards[beta]]", new[] { "cards" })]
    [InlineData("$zones[cards[beta]]", "$zones[cards[beta]]", new[] { "deck", "hand", "deck" })]
    [Theory]
    public void LiveZoneEndsNeedAWellFormedZoneTable(string from, string to, string[]? zones) {
        var (_, context, _) = Build();
        var transfer = new StateTransform.Transfer(
            from,
            to,
            ZoneSelector.First
        );

        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileAll(
            [new Rule(
                    Name(value: "move"),
                    [new ActionEffect.TransformState(Transform: transfer)],
                    Zones: zones
                )],
            context
        ));
    }
    [InlineData("$zone:cards:first")]
    [InlineData("$zone:missing:first")]
    [InlineData("$zone:deck:middle")]
    [InlineData("$zone:deck")]
    [InlineData("$zone:deck:first:extra")]
    [Theory]
    public void OnlyEndpointsOfDeclaredOrderedZonesCompile(string spelling) {
        var (_, context, _) = Build();
        Assert.Throws<RuleException>(testCode: () => Key(
            context: context,
            spelling: spelling
        ));
    }
    [Fact]
    public void PatternWorkUsesDeclaredCapacityAndOpcodeCostsAndTracksAllSources() {
        var (_, context, _) = Build();
        var expression = RuleCompiler.CompileExpression(
            ValueExpression.Parse(text: "cards[alpha] % 13"),
            CellKind.Int,
            "end-law",
            "value",
            context
        );
        var operand = new PatternOperand(
            "deck",
            null,
            null,
            default,
            "pattern",
            null,
            "cards",
            default,
            MatchFacet.Prefix,
            expression
        );
        // Three tokens, each with one match step and (read + constant + integer modulo).
        Assert.Equal(
            (3 * (((1 + 1) + 1) + 16)),
            operand.Cost(context: context)
        );
        List<RuleAccess> reads = [];

        operand.CollectReads(into: reads);
        Assert.Contains(
            new RuleAccess(
                "deck",
                null
            ),
            reads
        );
        Assert.Contains(
            new RuleAccess(
                "cards",
                null
            ),
            reads
        );
        Assert.Contains(
            new RuleAccess(
                "cards",
                "alpha"
            ),
            reads
        );
        var expensive = new PatternOperand(
            "deck",
            null,
            null,
            default,
            "pattern",
            null,
            null,
            default,
            MatchFacet.Prefix,
            [new CompiledExpressionToken(
                    ExpressionOp.Operand,
                    Operand: new MaxCostOperand()
                )]
        );

        Assert.Equal(
            long.MaxValue,
            expensive.Cost(context: context)
        );
    }

    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }
    private sealed class MaxCostOperand() : OperandFact(CellKind.Int) {
        public override long Cost(RuleCompileContext context) => long.MaxValue;
        public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(
            kind: CellKind.Int,
            value: 0
        );
    }
}
