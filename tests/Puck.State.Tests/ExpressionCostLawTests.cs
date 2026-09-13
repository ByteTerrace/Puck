using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// THE LAW: Legacy expression work weights remain deterministic heuristics until calibration is substantiated.
/// Every registered operation has a positive heuristic weight; those weights must not become reference cycles.
/// Division costs more than multiplication, which costs more than single-cycle ALU. Combinatorial algorithms and
/// iterative prime testing scale according to their static instruction and iteration complexity.
/// </summary>
public sealed class ExpressionCostLawTests {
    private static readonly RuleCompileContext Context = new(
        section: null,
        catalog: StateCatalog.Compile(section: null),
        tables: null,
        patterns: null,
        generators: null,
        simulationRateHz: 240,
        vocabulary: RuleVocabulary.Core
    );

    private static CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        return RuleCompiler.CompileExpression(
            expression: new ValueExpression(Tokens: tokens),
            kind: kind,
            ruleName: "law",
            verb: "law",
            context: Context
        );
    }

    [Fact]
    public void EveryExpressionOpcodeHasAStrictlyPositiveCost() {
        foreach (var op in Enum.GetValues<ExpressionOp>()) {
            var cost = RuleWorkBudget.OperationCost(operation: op);

            Assert.True(
                condition: (cost >= 1L),
                userMessage: $"Operation '{op}' must have a positive cost, but returned {cost}"
            );
        }
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

        Assert.Equal(
            actual: alu,
            expected: 1L
        );
        Assert.Equal(
            actual: select,
            expected: 2L
        );
        Assert.Equal(
            actual: popcnt,
            expected: 2L
        );
        Assert.Equal(
            actual: mul,
            expected: 3L
        );
        Assert.Equal(
            actual: pext,
            expected: 4L
        );
        Assert.Equal(
            actual: div,
            expected: 16L
        );
        Assert.Equal(
            actual: mod,
            expected: 16L
        );

        // Strict tier ordering
        Assert.True(condition: (alu < select));
        Assert.True(condition: (select <= mul));
        Assert.True(condition: (mul < pext));
        Assert.True(condition: (pext < div));
        Assert.True(condition: (div < sqrt));
        Assert.True(condition: (sqrt <= trig));
        Assert.True(condition: (trig < gcd));
        Assert.True(condition: (gcd < hilbert));
        Assert.True(condition: (hilbert < isPrime));
    }
    [Fact]
    public void HeuristicWeightsCannotMasqueradeAsCalibratedCycles() {
        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            Assert.True(condition: ReferenceSchedule.OperationCostBound(
                operation,
                CellKind.Int
            ).IsUnmodeled);
            Assert.True(condition: ReferenceSchedule.OperationCostBound(
                operation,
                CellKind.Fixed
            ).IsUnmodeled);
            Assert.InRange(
                RuleWorkBudget.OperationCost(operation),
                1L,
                (long.MaxValue - 1L)
            );
        }
        Assert.Null(@object: CostModel.Default.EvidenceDigest);
        Assert.True(condition: new RuleCost(
            Check: 1,
            Effects: 3,
            Setup: 0
        ).ToBound().IsUnmodeled);
        Assert.Equal(
            long.MaxValue,
            RuleWorkBudget.OperationCost(((ExpressionOp)byte.MaxValue))
        );
    }
    [Fact]
    public void RuntimeProgramsStillAccountForEveryOperation() {
        CompiledExpressionToken[] program = [
            new(
                ExpressionOp.Constant,
                Constant: 3
            ), new(
                ExpressionOp.Constant,
                Constant: 4
            ), new(ExpressionOp.Multiply),
            new(
                ExpressionOp.Constant,
                Constant: 2
            ), new(ExpressionOp.Divide),
        ];

        Assert.Equal(
            22L,
            RuleWorkBudget.ExpressionCost(
                context: Context,
                tokens: program
            )
        );
    }
    [InlineData("1 + 2")]
    [InlineData("3 * 4 / 2")]
    [InlineData("isPrime(17)")]
    [InlineData("greatestCommonDivisor(12, 18)")]
    [InlineData("replicationMask(8)")]
    [InlineData("repeatBits(127, 8)")]
    [Theory]
    public void SuccessfulConstantExpressionsCostOneConstant(string text) {
        Assert.Equal(
            1L,
            RuleWorkBudget.ExpressionCost(
                tokens: Compile(text),
                context: Context
            )
        );
    }
}
