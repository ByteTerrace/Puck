using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// THE LAW: Legacy expression work weights remain deterministic heuristics until calibration is substantiated.
/// Every registered operation has a positive heuristic weight; those weights must not become reference cycles.
/// Division costs more than multiplication, which costs more than single-cycle ALU. Combinatorial algorithms and
/// iterative prime testing scale according to their static instruction and iteration complexity.
/// </summary>
public sealed class ExpressionCostLawTests {
    private static readonly RuleCompileContext s_context = new(
        section: null,
        catalog: StateCatalog.Compile(section: null),
        tables: null,
        patterns: null,
        generators: null,
        simulationRateHz: 240,
        vocabulary: RuleVocabulary.Core
    );

    private static CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), error);
        return RuleCompiler.CompileExpression(
            expression: new ValueExpression(Tokens: tokens),
            kind: kind,
            ruleName: "law",
            verb: "law",
            context: s_context
        );
    }

    [Fact]
    public void EveryExpressionOpcodeHasAStrictlyPositiveCost() {
        foreach (var op in Enum.GetValues<ExpressionOp>()) {
            var cost = RuleWorkBudget.OperationCost(operation: op);
            Assert.True(cost >= 1L, $"Operation '{op}' must have a positive cost, but returned {cost}");
        }
    }

    [Fact]
    public void HeuristicWeightsCannotMasqueradeAsCalibratedCycles() {
        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            Assert.True(ReferenceSchedule.OperationCostBound(operation, CellKind.Int).IsUnmodeled);
            Assert.True(ReferenceSchedule.OperationCostBound(operation, CellKind.Fixed).IsUnmodeled);
            Assert.InRange(RuleWorkBudget.OperationCost(operation), 1L, long.MaxValue - 1L);
        }
        Assert.Null(CostModel.Default.EvidenceDigest);
        Assert.True(new RuleCost(0, 1, 3).ToBound().IsUnmodeled);
        Assert.Equal(long.MaxValue, RuleWorkBudget.OperationCost((ExpressionOp)byte.MaxValue));
    }
    [Fact]
    public void HeuristicOperationsRetainTheirExistingTierOrdering() {
        var alu = RuleWorkBudget.OperationCost(operation: ExpressionOp.Add);
        var select = RuleWorkBudget.OperationCost(operation: ExpressionOp.Select);
        var popcnt = RuleWorkBudget.OperationCost(operation: ExpressionOp.PopCount);
        var pext = RuleWorkBudget.OperationCost(operation: ExpressionOp.ParallelBitExtract);
        var mul = RuleWorkBudget.OperationCost(operation: ExpressionOp.Multiply);
        var div = RuleWorkBudget.OperationCost(operation: ExpressionOp.Divide);
        var mod = RuleWorkBudget.OperationCost(operation: ExpressionOp.Modulo);
        var sqrt = RuleWorkBudget.OperationCost(operation: ExpressionOp.SquareRoot);
        var trig = RuleWorkBudget.OperationCost(operation: ExpressionOp.Sine);
        var gcd = RuleWorkBudget.OperationCost(operation: ExpressionOp.GreatestCommonDivisor);
        var hilbert = RuleWorkBudget.OperationCost(operation: ExpressionOp.Hilbert);
        var isPrime = RuleWorkBudget.OperationCost(operation: ExpressionOp.IsPrime);

        Assert.Equal(1L, alu);
        Assert.Equal(2L, select);
        Assert.Equal(2L, popcnt);
        Assert.Equal(3L, mul);
        Assert.Equal(4L, pext);
        Assert.Equal(16L, div);
        Assert.Equal(16L, mod);

        // Strict tier ordering
        Assert.True(alu < select);
        Assert.True(select <= mul);
        Assert.True(mul < pext);
        Assert.True(pext < div);
        Assert.True(div < sqrt);
        Assert.True(sqrt <= trig);
        Assert.True(trig < gcd);
        Assert.True(gcd < hilbert);
        Assert.True(hilbert < isPrime);
    }

    [Theory]
    [InlineData("1 + 2")]
    [InlineData("3 * 4 / 2")]
    [InlineData("isPrime(17)")]
    [InlineData("gcd(12, 18)")]
    [InlineData("replicationMask(8)")]
    [InlineData("repeatBits(127, 8)")]
    public void SuccessfulConstantExpressionsCostOneConstant(string text) {
        Assert.Equal(1L, RuleWorkBudget.ExpressionCost(tokens: Compile(text), context: s_context));
    }

    [Fact]
    public void RuntimeProgramsStillAccountForEveryOperation() {
        CompiledExpressionToken[] program = [
            new(ExpressionOp.Constant, Constant: 3), new(ExpressionOp.Constant, Constant: 4), new(ExpressionOp.Multiply),
            new(ExpressionOp.Constant, Constant: 2), new(ExpressionOp.Divide),
        ];
        Assert.Equal(22L, RuleWorkBudget.ExpressionCost(tokens: program, context: s_context));
    }
}
