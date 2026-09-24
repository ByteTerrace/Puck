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
                program: out var parsed,
                text: text
            ),
            userMessage: error
        );
        return Rules.RuleCompiler.CompileExpression(
            context: Context,
            expression: parsed,
            kind: kind,
            ruleName: "law",
            verb: "law"
        );
    }

    [Fact]
    public void EveryExpressionOpcodeHasAStrictlyPositiveCost() {
        foreach (var op in Enum.GetValues<ExpressionOp>()) {
            var cost = Rules.RuleWorkBudget.OperationCost(operation: op).Units;

            Assert.True(
                condition: (cost >= 1L),
                userMessage: $"Operation '{op}' must have a positive cost, but returned {cost}"
            );
        }
    }
    [Fact]
    public void HeuristicOperationsRetainTheirExistingTierOrdering() {
        var alu = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Add).Units;
        var select = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Select).Units;
        var popcnt = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.SetBitCount).Units;
        var pext = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.ParallelBitExtract).Units;
        var mul = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Multiply).Units;
        var div = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Divide).Units;
        var mod = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Remainder).Units;
        var sqrt = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.SquareRoot).Units;
        var trig = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.Sine).Units;
        var gcd = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.GreatestCommonDivisor).Units;
        var hilbert = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.HilbertIndex).Units;
        var isPrime = Rules.RuleWorkBudget.OperationCost(operation: ExpressionOp.IsPrime).Units;

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
        // A coefficient is keyed by vocabulary and operation together: an operation name is unique only within the
        // vocabulary that registers it.
        var registered = ReferenceScheduleManifest.Coefficients.Where(predicate: coefficient => string.Equals(
            a: coefficient.Vocabulary,
            b: "expression",
            comparisonType: StringComparison.Ordinal
        )).ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: coefficient => coefficient,
            keySelector: coefficient => coefficient.Operation
        );

        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            // A price is answerable only where the manifest records evidence for it; the heuristic weight beside it
            // is never that answer.
            var coefficient = registered[operation.ToString()];

            foreach (var kind in new[] { CellKind.Int, CellKind.Fixed }) {
                var bound = ReferenceSchedule.OperationCostBound(kind: kind, operation: operation);
                var admitted = coefficient.NumericKinds.Contains(kind.ToString(), StringComparer.Ordinal);

                Assert.Equal(admitted, (bound == coefficient.Bound));
                if (!admitted) { Assert.True(condition: bound.IsUnmodeled); }
            }
            Assert.InRange(
                Rules.RuleWorkBudget.OperationCost(operation).Units,
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
        Assert.True(condition: Rules.RuleWorkBudget.OperationCost(((ExpressionOp)byte.MaxValue)).IsUnmodeled);
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
