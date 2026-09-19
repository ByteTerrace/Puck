using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// THE LAW: Legacy expression work weights remain deterministic heuristics until calibration is substantiated.
/// Every registered operation has a positive heuristic weight; those weights must not become reference cycles.
/// Division costs more than multiplication, which costs more than single-cycle ALU. Combinatorial algorithms and
/// iterative prime testing scale according to their static instruction and iteration complexity.
/// </summary>
public sealed class ExpressionCostLawTests {
    private static readonly Rules.RuleCompileContext Context = new(
        section: null,
        catalog: StateCatalog.Compile(section: null),
        tables: null,
        patterns: null,
        generators: null,
        simulationRateHz: 240,
        vocabulary: Rules.RuleVocabulary.Core
    );

    private static Rules.CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                program: out var parsed
            ),
            userMessage: error
        );
        return Rules.RuleCompiler.CompileExpression(
            expression: parsed,
            kind: kind,
            ruleName: "law",
            verb: "law",
            context: Context
        );
    }

    [Fact]
    public void EveryExpressionOpcodeHasAStrictlyPositiveCost() {
        foreach (var op in Enum.GetValues<ExpressionOp>()) {
            var cost = Rules.RuleWorkBudget.OperationCost(operation: op);

            Assert.True(
                condition: (cost >= 1L),
                userMessage: $"Operation '{op}' must have a positive cost, but returned {cost}"
            );
        }
    }
    [Fact]
    public void HeuristicOperationsRetainTheirExistingTierOrdering() {
        var alu = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Add);
        var select = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Select);
        var popcnt = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.PopCount);
        var pext = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.ParallelBitExtract);
        var mul = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Multiply);
        var div = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Divide);
        var mod = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Modulo);
        var sqrt = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.SquareRoot);
        var trig = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Sine);
        var gcd = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.GreatestCommonDivisor);
        var hilbert = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Hilbert);
        var isPrime = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.IsPrime);

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
        var registered = ReferenceScheduleManifest.Coefficients.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: coefficient => coefficient.Bound,
            keySelector: coefficient => coefficient.Operation
        );

        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            // A price is answerable only where the manifest records evidence for it; the heuristic weight beside it
            // is never that answer.
            var bound = ReferenceSchedule.OperationCostBound(
                operation,
                CellKind.Int
            );

            Assert.Equal(
                registered[operation.ToString()],
                bound
            );
            Assert.Equal(
                bound,
                ReferenceSchedule.OperationCostBound(
                    operation,
                    CellKind.Fixed
                )
            );
            Assert.InRange(
                Rules.RuleWorkBudget.OperationCost(operation),
                1L,
                (long.MaxValue - 1L)
            );
        }
        Assert.Equal(
            ReferenceScheduleManifest.Digest,
            CostModel.Default.EvidenceDigest
        );
        Assert.True(condition: new Rules.RuleCost(
            Check: 1,
            Effects: 3,
            Setup: 0
        ).ToBound().IsUnmodeled);
        Assert.Equal(
            long.MaxValue,
            Rules.RuleWorkBudget.OperationCost(((ExpressionOp)byte.MaxValue))
        );
    }
    [Fact]
    public void RuntimeProgramsStillAccountForEveryOperation() {
        Rules.CompiledExpressionToken[] program = [
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
            Rules.RuleWorkBudget.ExpressionCost(
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
            Rules.RuleWorkBudget.ExpressionCost(
                tokens: Compile(text),
                context: Context
            )
        );
    }
}
