using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The offscreen host renders at most one frame per step (<see cref="OffscreenTickHostedService.ComposesFrame"/>): a
/// pump call that ran a step, or a burst of them, composes one frame; one that ran none composes nothing, so no two
/// frames present one tick, except that a frame a step still owes (a capture armed at its tick, not yet served) is
/// composed again until one serves it; and a host stepping no simulation composes every call.
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
}
