using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The offscreen host renders at most one frame per step (<see cref="OffscreenTickHostedService.ComposesFrame"/>): a
/// pump call that ran a step, or a burst of them, composes one frame; one that ran none composes nothing, so no two
/// frames present one tick, except that a frame a step still owes (a capture armed at its tick, not yet served) is
/// composed again until one serves it; and a host stepping no simulation composes every call. A composed frame's
/// interval (<see cref="OffscreenFrameInterval"/>) spans every iteration since the frame before it.
/// </summary>
public sealed class OffscreenFrameCadenceLawTests {
    [InlineData(true, 1, false, true)]
    [InlineData(true, 4, false, true)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 0, true, true)]
    [InlineData(true, 1, true, true)]
    [InlineData(false, 0, false, true)]
    [Theory]
    public void AFrameIsComposedOnlyForAStepOrAFrameAStepOwes(bool hasSimulation, int stepsAdvanced, bool awaitsFrame, bool composes) =>
        Assert.Equal(
            actual: OffscreenTickHostedService.ComposesFrame(
                awaitsFrame: awaitsFrame,
                hasSimulation: hasSimulation,
                stepsAdvanced: stepsAdvanced
            ),
            expected: composes
        );
    /// <summary>A composed frame's interval spans every host-loop interval since the frame before it: iterations that
    /// compose nothing add their intervals to the next frame's, which then starts the next interval empty, and the span
    /// is clamped as the pump clamps a runaway interval.</summary>
    [Fact]
    public void AFramesIntervalSpansEveryIterationSinceTheFrameBeforeIt() {
        const ulong Max = 1000UL;
        var interval = new OffscreenFrameInterval();

        Assert.Equal(
            actual: new[] {
                interval.Take(composes: true, deltaTicks: 30UL, maxFrameTicks: Max),
                interval.Take(composes: false, deltaTicks: 40UL, maxFrameTicks: Max),
                interval.Take(composes: false, deltaTicks: 50UL, maxFrameTicks: Max),
                interval.Take(composes: true, deltaTicks: 60UL, maxFrameTicks: Max),
                interval.Take(composes: true, deltaTicks: 70UL, maxFrameTicks: Max),
                interval.Take(composes: false, deltaTicks: 900UL, maxFrameTicks: Max),
                interval.Take(composes: true, deltaTicks: 900UL, maxFrameTicks: Max),
                interval.Take(composes: true, deltaTicks: 5UL, maxFrameTicks: Max),
            },
            expected: [30UL, 0UL, 0UL, 150UL, 70UL, 0UL, Max, 5UL]
        );
    }
}
