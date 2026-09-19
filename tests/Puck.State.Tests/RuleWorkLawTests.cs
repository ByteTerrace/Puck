using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a work bound is a number, an unpriced operation, or an overflow, and the last two
/// survive every composition. Neither fits any ceiling, and neither reads back as a number.</summary>
public sealed class RuleWorkLawTests {
    [Fact]
    public void AnOverflowAndAnUnpricedOperationSurviveEveryComposition() {
        var known = RuleWork.Known(units: 7L);
        var unmodeled = RuleWork.Unmodeled(reason: "operation 255");

        Assert.True(condition: ((RuleWork.Known(units: long.MaxValue) + known).IsOverflow));
        Assert.True(condition: ((long.MaxValue * RuleWork.Known(units: 2L)).IsOverflow));
        Assert.True(condition: RuleWork.Known(units: -1L).IsOverflow);
        Assert.True(condition: (known * -1L).IsOverflow);

        foreach (var composed in new[] {
            (known + unmodeled),
            (unmodeled + known),
            (3L * unmodeled),
            RuleWork.Max(
                left: known,
                right: unmodeled
            ),
        }) {
            Assert.True(condition: composed.IsUnmodeled);
            Assert.Equal(
                "operation 255",
                composed.Reason
            );
        }

        Assert.True(condition: ((unmodeled + RuleWork.Overflow).IsOverflow));
        Assert.True(condition: RuleWork.Max(
            left: RuleWork.Overflow,
            right: known
        ).IsOverflow);
    }
    [Fact]
    public void ACountProvedZeroRemovesWhatItMultiplies() {
        Assert.Equal(
            RuleWork.Zero,
            (0L * RuleWork.Unmodeled(reason: "never reached"))
        );
        Assert.Equal(
            RuleWork.Zero,
            (RuleWork.Overflow * 0L)
        );
    }
    [Fact]
    public void OnlyAKnownBoundFitsACeilingOrReadsBackAsUnits() {
        Assert.True(condition: RuleWork.Known(units: long.MaxValue).Fits(ceiling: long.MaxValue));
        Assert.False(condition: RuleWork.Known(units: 5L).Fits(ceiling: 4L));
        Assert.False(condition: RuleWork.Overflow.Fits(ceiling: long.MaxValue));
        Assert.False(condition: RuleWork.Unmodeled(reason: "unpriced").Fits(ceiling: long.MaxValue));
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => RuleWork.Overflow.Units);
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => RuleWork.Unmodeled(reason: "unpriced").Units);
    }
    [Fact]
    public void TheCostlierBoundOrdersLastWhateverItsKind() {
        var ordered = new[] {
            RuleWork.Known(units: 1L),
            RuleWork.Known(units: long.MaxValue),
            RuleWork.Unmodeled(reason: "unpriced"),
            RuleWork.Overflow,
        };

        for (var index = 1; (index < ordered.Length); index++) {
            Assert.True(condition: (RuleWork.Compare(
                left: ordered[(index - 1)],
                right: ordered[index]
            ) < 0));
            Assert.True(condition: (RuleWork.Compare(
                left: ordered[index],
                right: ordered[(index - 1)]
            ) > 0));
        }
    }
}
