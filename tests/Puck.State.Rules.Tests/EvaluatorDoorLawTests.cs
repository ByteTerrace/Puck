using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: every write the evaluator makes goes through the arena's own admission door, so a
/// row's symbolic domain refuses an out-of-range value at runtime and not only in a law; and the trace records what
/// each binding, conjunct and effect did, with every number in the invariant culture.</summary>
public sealed class EvaluatorDoorLawTests {
    private static StateSection Symbolic() => new(
        Enums: [new StateEnum(
                Name: RulesFixture.Name(value: "suit"),
                Members: [
                    RulesFixture.Name(value: "clubs"),
                    RulesFixture.Name(value: "diamonds"),
                ]
            )],
        Rows: [
            new StateRow(
                Name: RulesFixture.Name(value: "suit"),
                Kind: CellKind.Int,
                Enum: RulesFixture.Name(value: "suit"),
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            ),
            EvaluatorFixture.Slot(
                name: "score",
                value: 0L
            ),
        ]
    );

    [Fact]
    public void AWriteOutsideTheRowsSymbolicDomainIsRefusedAtRuntime() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                    Name: RulesFixture.Name(value: "deal"),
                    Effects: [
                        EvaluatorFixture.Set(
                            row: "score",
                            value: 1m
                        ),
                        EvaluatorFixture.Set(
                            row: "suit",
                            value: 7m
                        ),
                    ]
                )],
            section: Symbolic()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        var refusals = evaluator.Diagnostics();

        Assert.Single(collection: refusals);
        Assert.Equal(
            actual: refusals[0].Refusal,
            expected: RuleEffectRefusal.MutationRejected
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "suit"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
    }
    [Fact]
    public void AWriteInsideTheRowsSymbolicDomainLands() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                    Name: RulesFixture.Name(value: "deal"),
                    Effects: [EvaluatorFixture.Set(
                            row: "suit",
                            value: 1m
                        )]
                )],
            section: Symbolic()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "suit"
            ),
            expected: 1L
        );
    }
    [Fact]
    public void ATraceRecordsTheBindingTheConjunctAndTheEffect() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "count"),
                Effects: [EvaluatorFixture.Add(
                        row: "score",
                        value: 2m
                    )],
                Locals: [new RuleLocal(
                        Expression: RulesFixture.Program(text: "3 + 4"),
                        Kind: CellKind.Int,
                        Name: RulesFixture.Name(value: "step")
                    )],
                Gate: EvaluatorFixture.Compare(
                    comparison: ExpressionOp.Equal,
                    row: "flag",
                    value: 0m
                )
            )]);

        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "count"
        ));
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        var captured = Assert.Single(collection: evaluator.TraceCaptured);

        Assert.True(condition: captured.GateOpen);
        Assert.Equal<string>(
            actual: captured.Locals,
            expected: ["step=7"]
        );
        Assert.Single(collection: captured.Conjuncts);
        Assert.Single(collection: captured.Effects);
        Assert.Contains(
            actualString: captured.Effects[0],
            expectedSubstring: "= 2"
        );
        Assert.Contains(
            actualString: captured.Effects[0],
            expectedSubstring: "applied"
        );
        Assert.Contains(
            actualString: captured.Describe(
                rule: "count",
                verb: "world.rule.trace"
            ),
            expectedSubstring: "tick=0"
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 2L
        );
    }
    [Fact]
    public void AnUnboundArmIsACountedRefusalRatherThanASilentSuccess() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                    Name: RulesFixture.Name(value: "stamp"),
                    Effects: [new IrreversibleFixture.Stamp()]
                )],
            vocabulary: new RuleVocabulary(
                effects: [new UnboundArm()],
                keys: [],
                operands: [],
                predicates: []
            )
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        var refusals = evaluator.Diagnostics();

        Assert.Single(collection: refusals);
        Assert.Equal(
            actual: refusals[0].Refusal,
            expected: StateEffectRefusal.ArmUnbound
        );
    }

    private sealed class UnboundArm : EffectFamily {
        public override string Discriminator => "stamp";
        public override Type EffectType => typeof(IrreversibleFixture.Stamp);

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => new UnboundEffect();
    }
    private sealed class UnboundEffect : RuleEffect {
        public UnboundEffect() : base(describe: "stamp") { }

        public override RuleWork Cost(IRuleCostContext context) => 1L;
    }
}
