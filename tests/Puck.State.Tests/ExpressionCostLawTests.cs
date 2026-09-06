using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// THE LAW: Numeric expression costing is derived from static disassembly analysis, not wall-clock execution.
/// Every operation has a strictly positive, calibrated work-unit cost matching its instruction footprint and loop bounds.
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
    public void OperationsReflectDisassemblyTierOrdering() {
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

    [Fact]
    public void CompiledExpressionsAccountForTheSumOfTheirOperations() {
        // "1 + 2": Constant (1) + Constant (1) + Add (1) = 3
        var simple = Compile("1 + 2");
        Assert.Equal(3L, RuleWorkBudget.ExpressionCost(tokens: simple, context: s_context));

        // "3 * 4 / 2": Constant (1) + Constant (1) + Mul (3) + Constant (1) + Div (16) = 22
        var arithmetic = Compile("3 * 4 / 2");
        Assert.Equal(1L + 1L + 3L + 1L + 16L, RuleWorkBudget.ExpressionCost(tokens: arithmetic, context: s_context));

        // "isPrime(17)": Constant (1) + IsPrime (200) = 201
        var prime = Compile("isPrime(17)");
        Assert.Equal(1L + 200L, RuleWorkBudget.ExpressionCost(tokens: prime, context: s_context));

        // "gcd(12, 18)": Constant (1) + Constant (1) + GCD (40) = 42
        var gcdExpr = Compile("gcd(12, 18)");
        Assert.Equal(1L + 1L + 40L, RuleWorkBudget.ExpressionCost(tokens: gcdExpr, context: s_context));
    }
}
