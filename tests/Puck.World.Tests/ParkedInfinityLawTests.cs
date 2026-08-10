using Xunit;

using Puck.Maths;

namespace Puck.World.Tests;

/// <summary>
/// Proves the <c>$parked:</c> forever contract's comparison half: positive infinity (the forever-parked fact)
/// compares as strictly greater than every finite value and equal to itself, through the
/// <see cref="ActionStateComparisons"/> infinity-aware overload the rule gate rides. Exhaustive over the whole
/// comparison vocabulary times all three infinity placements, because the vocabulary is closed and small enough
/// that sampling it would be a choice, not a constraint. The ruling this pins: a rule "while parked
/// (remaining &gt; 0)" HOLDS for a forever-parked seat — it IS parked, maximally — where a no-fact or sentinel
/// encoding would have read the most-parked seat of all as not parked, or as equal to one particular number.
/// </summary>
public sealed class ParkedInfinityLawTests {
    private static readonly FixedQ4816 s_zero = FixedQ4816.Zero;
    private static readonly FixedQ4816 s_large = FixedQ4816.FromInteger(value: long.MaxValue >> 17);

    [Theory]
    [InlineData(ActionStateComparison.Equal, false)]
    [InlineData(ActionStateComparison.NotEqual, true)]
    [InlineData(ActionStateComparison.Less, false)]
    [InlineData(ActionStateComparison.LessOrEqual, false)]
    [InlineData(ActionStateComparison.Greater, true)]
    [InlineData(ActionStateComparison.GreaterOrEqual, true)]
    public void ForeverAgainstEveryFiniteValue_ComparesAsStrictlyGreater(ActionStateComparison comparison, bool expected) {
        // The tent-law case: forever > 0 holds (it IS parked), and no finite magnitude changes any verdict.
        Assert.Equal(expected: expected, actual: comparison.Holds(value: s_zero, valueIsForever: true, expected: s_zero, expectedIsForever: false));
        Assert.Equal(expected: expected, actual: comparison.Holds(value: s_zero, valueIsForever: true, expected: s_large, expectedIsForever: false));
    }

    [Theory]
    [InlineData(ActionStateComparison.Equal, false)]
    [InlineData(ActionStateComparison.NotEqual, true)]
    [InlineData(ActionStateComparison.Less, true)]
    [InlineData(ActionStateComparison.LessOrEqual, true)]
    [InlineData(ActionStateComparison.Greater, false)]
    [InlineData(ActionStateComparison.GreaterOrEqual, false)]
    public void EveryFiniteValueAgainstForever_ComparesAsStrictlyLess(ActionStateComparison comparison, bool expected) {
        Assert.Equal(expected: expected, actual: comparison.Holds(value: s_zero, valueIsForever: false, expected: s_zero, expectedIsForever: true));
        Assert.Equal(expected: expected, actual: comparison.Holds(value: s_large, valueIsForever: false, expected: s_zero, expectedIsForever: true));
    }

    [Theory]
    [InlineData(ActionStateComparison.Equal, true)]
    [InlineData(ActionStateComparison.NotEqual, false)]
    [InlineData(ActionStateComparison.Less, false)]
    [InlineData(ActionStateComparison.LessOrEqual, true)]
    [InlineData(ActionStateComparison.Greater, false)]
    [InlineData(ActionStateComparison.GreaterOrEqual, true)]
    public void ForeverAgainstForever_ComparesAsEqual(ActionStateComparison comparison, bool expected) {
        Assert.Equal(expected: expected, actual: comparison.Holds(value: s_zero, valueIsForever: true, expected: s_zero, expectedIsForever: true));
    }

    [Fact]
    public void BothFinite_DelegatesToTheOrdinaryComparison() {
        var three = FixedQ4816.FromInteger(value: 3);
        var five = FixedQ4816.FromInteger(value: 5);

        Assert.True(condition: ActionStateComparison.Less.Holds(value: three, valueIsForever: false, expected: five, expectedIsForever: false));
        Assert.False(condition: ActionStateComparison.Greater.Holds(value: three, valueIsForever: false, expected: five, expectedIsForever: false));
        Assert.True(condition: ActionStateComparison.Equal.Holds(value: five, valueIsForever: false, expected: five, expectedIsForever: false));
    }
}
