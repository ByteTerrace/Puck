using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins <see cref="HashTrace.FirstDivergence"/>, the answer a replay verdict reports as "diverged at tick N":
/// the three shapes two traces can be in — equal, differing at some tick, and one a prefix of the other — plus the
/// null refusals.</summary>
public sealed class HashTraceTests {
    [Fact]
    public void IdenticalTracesReportNoDivergence() {
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [1UL, 2UL, 3UL], right: [1UL, 2UL, 3UL]), expected: -1);
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [], right: []), expected: -1);
    }
    [Fact]
    public void TheFirstDifferingTickIsTheAnswerEvenWhenLaterTicksAgreeAgain() {
        // A replay that drifts and then coincidentally re-agrees still diverged where it first did; reporting the
        // LAST difference would send a reader to the wrong tick.
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [1UL, 2UL, 3UL], right: [1UL, 9UL, 3UL]), expected: 1);
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [7UL], right: [8UL]), expected: 0);
    }
    [Fact]
    public void APrefixDivergesAtTheShorterLength() {
        // A run that stopped early agrees everywhere it has an answer, so the divergence is the first tick the shorter
        // trace has no hash for — not -1, which would call a truncated replay identical.
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [1UL, 2UL], right: [1UL, 2UL, 3UL]), expected: 2);
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [1UL, 2UL, 3UL], right: [1UL, 2UL]), expected: 2);
        Assert.Equal(actual: HashTrace.FirstDivergence(left: [], right: [1UL]), expected: 0);
    }
    [Fact]
    public void ANullTraceIsRefusedRatherThanReadAsEmpty() {
        _ = Assert.Throws<ArgumentNullException>(testCode: () => HashTrace.FirstDivergence(left: null!, right: []));
        _ = Assert.Throws<ArgumentNullException>(testCode: () => HashTrace.FirstDivergence(left: [], right: null!));
    }
}
