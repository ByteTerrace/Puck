using Xunit;

using Puck.Maths;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Proves the <c>$parked:</c> forever contract's comparison half: positive infinity (the forever-parked fact)
/// compares as strictly greater than every finite value and equal to itself, through the
/// <see cref="ExpressionComparisons"/> infinity-aware overload the rule gate rides. Exhaustive over the whole
/// comparison vocabulary times all three infinity placements, because the vocabulary is closed and small enough
/// that sampling it would be a choice, not a constraint. The ruling this pins: a rule "while parked
/// (remaining &gt; 0)" HOLDS for a forever-parked seat — it IS parked, maximally — where a no-fact or sentinel
/// encoding would have read the most-parked seat of all as not parked, or as equal to one particular number.
/// </summary>
public sealed class ParkedInfinityLawTests {
    private static readonly FixedQ4816 Zero = FixedQ4816.Zero;
    private static readonly FixedQ4816 Large = FixedQ4816.FromInteger(value: (long.MaxValue >> 17));

    [Fact]
    public void BothFinite_DelegatesToTheOrdinaryComparison() {
        var three = FixedQ4816.FromInteger(value: 3);
        var five = FixedQ4816.FromInteger(value: 5);

        Assert.True(condition: ExpressionOp.Less.Holds(
            expected: five,
            expectedIsForever: false,
            value: three,
            valueIsForever: false
        ));
        Assert.False(condition: ExpressionOp.Greater.Holds(
            expected: five,
            expectedIsForever: false,
            value: three,
            valueIsForever: false
        ));
        Assert.True(condition: ExpressionOp.Equal.Holds(
            expected: five,
            expectedIsForever: false,
            value: five,
            valueIsForever: false
        ));
    }
    [InlineData(ExpressionOp.Equal, false)]
    [InlineData(ExpressionOp.NotEqual, true)]
    [InlineData(ExpressionOp.Less, true)]
    [InlineData(ExpressionOp.LessOrEqual, true)]
    [InlineData(ExpressionOp.Greater, false)]
    [InlineData(ExpressionOp.GreaterOrEqual, false)]
    [Theory]
    public void EveryFiniteValueAgainstForever_ComparesAsStrictlyLess(ExpressionOp comparison, bool expected) {
        Assert.Equal(
            expected: expected,
            actual: comparison.Holds(
                expected: Zero,
                expectedIsForever: true,
                value: Zero,
                valueIsForever: false
            )
        );
        Assert.Equal(
            expected: expected,
            actual: comparison.Holds(
                expected: Zero,
                expectedIsForever: true,
                value: Large,
                valueIsForever: false
            )
        );
    }
    [InlineData(ExpressionOp.Equal, false)]
    [InlineData(ExpressionOp.NotEqual, true)]
    [InlineData(ExpressionOp.Less, false)]
    [InlineData(ExpressionOp.LessOrEqual, false)]
    [InlineData(ExpressionOp.Greater, true)]
    [InlineData(ExpressionOp.GreaterOrEqual, true)]
    [Theory]
    public void ForeverAgainstEveryFiniteValue_ComparesAsStrictlyGreater(ExpressionOp comparison, bool expected) {
        // The tent-law case: forever > 0 holds (it IS parked), and no finite magnitude changes any verdict.
        Assert.Equal(
            expected: expected,
            actual: comparison.Holds(
                expected: Zero,
                expectedIsForever: false,
                value: Zero,
                valueIsForever: true
            )
        );
        Assert.Equal(
            expected: expected,
            actual: comparison.Holds(
                expected: Large,
                expectedIsForever: false,
                value: Zero,
                valueIsForever: true
            )
        );
    }
    [InlineData(ExpressionOp.Equal, true)]
    [InlineData(ExpressionOp.NotEqual, false)]
    [InlineData(ExpressionOp.Less, false)]
    [InlineData(ExpressionOp.LessOrEqual, true)]
    [InlineData(ExpressionOp.Greater, false)]
    [InlineData(ExpressionOp.GreaterOrEqual, true)]
    [Theory]
    public void ForeverAgainstForever_ComparesAsEqual(ExpressionOp comparison, bool expected) {
        Assert.Equal(
            expected: expected,
            actual: comparison.Holds(
                expected: Zero,
                expectedIsForever: true,
                value: Zero,
                valueIsForever: true
            )
        );
    }
}
