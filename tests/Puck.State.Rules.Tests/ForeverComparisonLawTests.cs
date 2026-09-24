using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: <c>forever</c> is a fact, positive infinity, not a number. A gate conjunct that
/// compares a <c>forever</c> operand directly answers under infinity semantics and records nothing in the refusal
/// ledger; a numeric expression that reads the same operand cannot evaluate, so its conjunct reads false and
/// records <see cref="RuleEffectRefusal.Arithmetic"/>.</summary>
public sealed class ForeverComparisonLawTests {
    // The one operand that answers forever: the library itself declares none, so the law carries its own.
    private sealed class ForeverOperand() : RuleOperand(valueKind: CellKind.Int) {
        public override RuleWork Cost(IRuleCostContext context) => 1L;
        public override RuleFact Read(IStateReader reader) => RuleFact.Forever(kind: CellKind.Int);
    }

    private static GateToken[] Gate(CompiledValueSource left, ExpressionOp comparison) => [new GateToken(
        Comparison: comparison,
        Describe: "forever",
        LeftSource: left,
        RightSource: CompiledValueSource.Constant(rawValue: 5L),
        ValueKind: CellKind.Int
    )];

    [InlineData(ExpressionOp.Greater, true)]
    [InlineData(ExpressionOp.GreaterOrEqual, true)]
    [InlineData(ExpressionOp.NotEqual, true)]
    [InlineData(ExpressionOp.Less, false)]
    [InlineData(ExpressionOp.LessOrEqual, false)]
    [InlineData(ExpressionOp.Equal, false)]
    [Theory]
    public void AGateComparingAForeverOperandAnswersUnderInfinityAndRecordsNoRefusal(ExpressionOp comparison, bool expected) {
        var (_, evaluator, _, _, _) = EvaluatorFixture.Arrange(rules: []);
        var open = evaluator.GateOpen(
            faulted: out var faulted,
            gate: Gate(
                comparison: comparison,
                left: CompiledValueSource.FromOperand(operand: new ForeverOperand())
            ),
            ruleName: "compare"
        );

        Assert.Equal(
            actual: open,
            expected: expected
        );
        Assert.False(condition: faulted);
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void ANumericExpressionReadingAForeverOperandRefusesAsArithmetic() {
        var (_, evaluator, _, _, _) = EvaluatorFixture.Arrange(rules: []);
        var open = evaluator.GateOpen(
            faulted: out var faulted,
            gate: Gate(
                comparison: ExpressionOp.Greater,
                left: CompiledValueSource.FromExpression(expression: [new CompiledExpressionToken(
                        Operand: new ForeverOperand(),
                        Operation: ExpressionOp.Operand
                    )])
            ),
            ruleName: "compute"
        );

        Assert.False(condition: open);
        Assert.True(condition: faulted);

        var refusal = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal(
            actual: refusal.Refusal,
            expected: RuleEffectRefusal.Arithmetic
        );
    }
}
