using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a program carrying an operation nothing prices makes its rule, and the whole sheet,
/// unmodeled by that operation's name; a count too large to price overflows the sheet instead of wrapping; and a
/// sort is priced at the comparisons and moves its implementation can spend, never at a linear pass.</summary>
public sealed class WorkSheetBoundLawTests {
    private static readonly ExpressionOp Unregistered = ((ExpressionOp)byte.MaxValue);

    private static (RuleWork Work, RuleCompileContext Context) Sheet(CompiledRule rule, long multiplier) {
        var context = EvaluatorFixture.Context(section: EvaluatorFixture.Section());
        var line = RuleWorkBudget.Contributor(
            context: context,
            isInteraction: false,
            multiplier: multiplier,
            rule: rule
        );
        var (_, work) = RuleWorkBudget.Tally(
            contributors: [line],
            writers: RuleWorkBudget.CountWriters(rules: [(rule, multiplier)])
        );

        return (work, context);
    }
    private static CompiledRule Compile(params ActionEffect[] effects) => RuleCompiler.CompileAll(
        context: EvaluatorFixture.Context(section: EvaluatorFixture.Section()),
        rules: [new Rule(
                Effects: effects,
                Name: RulesFixture.Name(value: "priced")
            )]
    )[0];

    [Fact]
    public void AnOperationNothingPricesMakesTheWholeSheetUnmodeledByName() {
        var rule = Compile(effects: EvaluatorFixture.Add(
            row: "score",
            value: 1m
        ));
        var unpriced = (rule with {
            Locals = [new CompiledRuleLocal(
                    Expression: [new CompiledExpressionToken(Operation: Unregistered)],
                    Kind: CellKind.Int,
                    Name: "unpriced"
                )],
        });
        var (work, _) = Sheet(
            multiplier: 1L,
            rule: unpriced
        );

        Assert.True(condition: work.IsUnmodeled);
        Assert.Contains(
            expectedSubstring: "255",
            actualString: work.Reason
        );
        Assert.False(condition: work.Fits(ceiling: long.MaxValue));
        Assert.True(condition: Sheet(
            multiplier: 1L,
            rule: rule
        ).Work.IsKnown);
    }
    [Fact]
    public void ACountTooLargeToPriceOverflowsTheSheet() {
        var rule = Compile(effects: EvaluatorFixture.Add(
            row: "score",
            value: 1m
        ));
        var (work, _) = Sheet(
            multiplier: long.MaxValue,
            rule: rule
        );

        Assert.True(condition: work.IsOverflow);
        Assert.False(condition: work.Fits(ceiling: long.MaxValue));
    }
    [Theory]
    [InlineData(0L, 1, 0L)]
    [InlineData(1L, 1, 3L)]
    [InlineData(4L, 1, 30L)]
    [InlineData(5L, 2, 60L)]
    public void AnInsertionSortIsPricedAtEveryPairAReversedInputCompares(long count, int keys, long expected) => Assert.Equal(
        RuleWork.Known(units: expected),
        RuleWorkBudget.InsertionSortWork(
            count: count,
            keys: keys
        )
    );
    [Fact]
    public void ASortEnvelopeGrowsFasterThanItsInputAndOverflowsRatherThanWraps() {
        Assert.Equal(
            RuleWork.Zero,
            RuleWorkBudget.IntrosortWork(count: 1L)
        );
        // 2 x 16 x (6 x (floor(log2 16) + 1) + 8).
        Assert.Equal(
            RuleWork.Known(units: 1_216L),
            RuleWorkBudget.IntrosortWork(count: 16L)
        );
        Assert.True(condition: ((2L * RuleWorkBudget.IntrosortWork(count: 1_024L).Units) < RuleWorkBudget.IntrosortWork(count: 2_048L).Units));
        Assert.True(condition: RuleWorkBudget.IntrosortWork(count: long.MaxValue).IsOverflow);
        Assert.True(condition: RuleWorkBudget.InsertionSortWork(
            count: long.MaxValue,
            keys: 1
        ).IsOverflow);
    }
    [Fact]
    public void ASortingTransformIsPricedAtItsInsertionSort() {
        static StateRow Keyed(string name, int capacity) => new(
            Name: RulesFixture.Name(value: name),
            Kind: CellKind.Int,
            Capacity: capacity,
            Cells: [new StateCell(
                    Key: RulesFixture.Name(value: "a"),
                    Value: CellValue.Int(value: 1L)
                )]
        );

        var section = new StateSection(Rows: [.. (EvaluatorFixture.Section().Rows ?? []), Keyed(
                capacity: 12,
                name: "pile"
            ), Keyed(
                capacity: 4,
                name: "heap"
            )]);
        var context = EvaluatorFixture.Context(section: section);

        long Sorting(string row) => RuleCompiler.CompileAll(
            context: context,
            rules: [new Rule(
                    Effects: [new ActionEffect.TransformState(Transform: new StateTransform.SortKeyed(Row: row))],
                    Name: RulesFixture.Name(value: "sorting")
                )]
        )[0].CostBreakdown(context: context).Effects.Units;

        // Both sorts pay the same transaction floor over the same section; they differ by the insertion sort of
        // each row at its full capacity.
        Assert.Equal(
            (RuleWorkBudget.InsertionSortWork(
                count: 12L,
                keys: 1
            ).Units - RuleWorkBudget.InsertionSortWork(
                count: 4L,
                keys: 1
            ).Units),
            (Sorting(row: "pile") - Sorting(row: "heap"))
        );
    }
}
