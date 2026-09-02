using Xunit;

using Puck.SdfVm.Views;

namespace Puck.SdfVm.Tests;

/// <summary>Pins <see cref="FirstOrderLag.Alpha"/>'s contract at its own boundaries: the documented clamp for a
/// non-positive rate or elapsed time, the non-finite policy (a <c>NaN</c> operand reads as non-positive; a
/// positive-infinity operand paired with a strictly positive other one reads as immediate full catch-up), and that
/// every result stays inside the closed <c>[0, 1]</c> interval — never <see cref="float.NaN"/>.</summary>
public sealed class FirstOrderLagTests {
    [InlineData(0f, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(1f, -1f)]
    [InlineData(-1f, -1f)]
    [Theory]
    public void NonPositiveRateOrDeltaClampsToZero(float rate, float deltaSeconds) {
        Assert.Equal(expected: 0f, actual: FirstOrderLag.Alpha(deltaSeconds: deltaSeconds, rate: rate));
    }
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.NaN)]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.NaN, -1f)]
    [Theory]
    public void NaNOperandReadsAsNonPositive(float rate, float deltaSeconds) {
        Assert.Equal(expected: 0f, actual: FirstOrderLag.Alpha(deltaSeconds: deltaSeconds, rate: rate));
    }
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [Theory]
    public void PositiveInfiniteOperandWithAPositiveOtherReadsAsImmediateCatchUp(float rate, float deltaSeconds) {
        Assert.Equal(expected: 1f, actual: FirstOrderLag.Alpha(deltaSeconds: deltaSeconds, rate: rate));
    }
    // The exact scenario an unclamped rate*deltaSeconds product reaches Infinity*0 = NaN at: a positive-infinity
    // rate with NO elapsed time at all. Zero elapsed time means zero catch-up regardless of rate, so this reads as
    // the ordinary zero-delta clamp, not as the infinite-rate case above.
    [Fact]
    public void PositiveInfiniteRateWithZeroDeltaIsZeroNotNaN() {
        Assert.Equal(expected: 0f, actual: FirstOrderLag.Alpha(deltaSeconds: 0f, rate: float.PositiveInfinity));
    }
    [Fact]
    public void ZeroRateWithPositiveInfiniteDeltaIsZeroNotNaN() {
        Assert.Equal(expected: 0f, actual: FirstOrderLag.Alpha(deltaSeconds: float.PositiveInfinity, rate: 0f));
    }
    [InlineData(1f, (1f / 60f))]
    [InlineData(30f, (1f / 240f))]
    [InlineData(0.001f, 1000f)]
    [Theory]
    public void FiniteInputsStayInsideTheClosedUnitInterval(float rate, float deltaSeconds) {
        var alpha = FirstOrderLag.Alpha(deltaSeconds: deltaSeconds, rate: rate);

        Assert.False(condition: float.IsNaN(f: alpha));
        Assert.InRange(actual: alpha, high: 1f, low: 0f);
    }
}
