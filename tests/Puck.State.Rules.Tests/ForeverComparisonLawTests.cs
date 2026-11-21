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
        public override Fact Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys) => Fact.Forever(kind: CellKind.Int);
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
        // The operand spelled alone and spelled as a program of one read are one comparison.
        foreach (var left in (CompiledValueSource[])[
            CompiledValueSource.FromOperand(operand: new ForeverOperand()),
            CompiledValueSource.FromExpression(expression: [new CompiledExpressionToken(Operand: new ForeverOperand(), Operation: ExpressionOp.Operand)], kind: CellKind.Int),
        ]) {
            var (_, evaluator, _, _, _) = EvaluatorFixture.Arrange(rules: []);
            var verdict = evaluator.Decide(
                gate: Gate(
                    comparison: comparison,
                    left: left
                ),
                ruleName: "compare"
            );

            Assert.Equal(
                actual: verdict,
                expected: (expected
                    ? Verdict.Open
                    : Verdict.Closed)
            );
            Assert.Empty(collection: evaluator.Diagnostics());
        }
    }
    [Fact]
    public void ANumericExpressionReadingAForeverOperandRefusesAsArithmetic() {
        var (_, evaluator, _, _, _) = EvaluatorFixture.Arrange(rules: []);
        var verdict = evaluator.Decide(
            gate: Gate(
                comparison: ExpressionOp.Greater,
                // A lone read is the fact itself (CompiledValueSource.FromExpression); computing with it is not.
                left: CompiledValueSource.FromExpression(expression: [
                    new CompiledExpressionToken(Operand: new ForeverOperand(), Operation: ExpressionOp.Operand),
                    new CompiledExpressionToken(Constant: 0L, Operation: ExpressionOp.Constant),
                    new CompiledExpressionToken(Operation: ExpressionOp.Add),
                ], kind: CellKind.Int)
            ),
            ruleName: "compute"
        );

        Assert.Equal(
            actual: verdict,
            expected: Verdict.Faulted
        );

        var refusal = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal(
            actual: refusal.Refusal,
            expected: RuleEffectRefusal.Arithmetic
        );
    }
}
