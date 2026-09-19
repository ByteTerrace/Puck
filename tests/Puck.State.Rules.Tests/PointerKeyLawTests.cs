using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a pointer resolves to a cell name whether or not any key table interns it, so a
/// read through one answers zero for the cell no row holds; only an address that names nothing at all — an empty
/// ordered zone's endpoint — answers the absent fact that faults the arithmetic spelled around it.</summary>
public sealed class PointerKeyLawTests {
    [Fact]
    public void APointerNamingACellNoRowHoldsReadsZeroRatherThanNoCellAtAll() {
        var (host, evaluator) = Fire(expression: "board[$cell:mask:$value] + 1");

        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "rankValue"
            ),
            expected: 1L
        );
    }
    [Fact]
    public void AnEmptyOrderedZonesEndpointStillNamesNoCellAndFaultsTheArithmetic() {
        var (host, evaluator) = Fire(expression: "board[$zone:hand:last] + 1");

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "rankValue"
            ),
            expected: 0L
        );
        Assert.Contains(
            collection: evaluator.Diagnostics(),
            filter: static diagnostic => diagnostic.Detail.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "named no cell"
            )
        );
    }

    private static (ArenaEffectHost Host, RuleEvaluator Evaluator) Fire(string expression) {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [new Rule(
                    Name: TransformFixture.Name(value: "point"),
                    Effects: [new ActionEffect.SetState(
                            Expression: ExpressionProgram.Parse(text: expression),
                            State: "rankValue"
                        )]
                )]
        );
        var host = TransformFixture.Host(
            context: context,
            section: section
        );
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        );

        return (host, evaluator);
    }
}
