using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>Lockstep presentation (<see cref="PresentPacingControl.TickLocked"/>): a frame steps at most once, presents
/// the completed tick with no interpolation, and spans exactly the simulation time it stepped; without it a frame takes
/// the runaway clamp, the pump's remainder and the wall interval.</summary>
public sealed class LockstepPresentationLawTests {
    [Fact]
    public void ALockstepFrameStepsOnceAndSpansWhatItStepped() {
        var pacing = new PresentPacingControl(initialTargetHertz: null);

        Assert.False(condition: pacing.TickLocked);
        pacing.SetTickLocked(locked: true);
        Assert.True(condition: pacing.TickLocked);

        Assert.Equal(expected: 1680UL, actual: PresentPacingControl.FrameClampOf(maxFrameTicks: 12600UL, stepTicks: 1680UL, tickLocked: true));
        Assert.Equal(expected: 12600UL, actual: PresentPacingControl.FrameClampOf(maxFrameTicks: 12600UL, stepTicks: 1680UL, tickLocked: false));
        Assert.Equal(expected: 0UL, actual: PresentPacingControl.AccumulatorOf(accumulatorTicks: 900UL, tickLocked: true));
        Assert.Equal(expected: 900UL, actual: PresentPacingControl.AccumulatorOf(accumulatorTicks: 900UL, tickLocked: false));
        Assert.Equal(expected: 1680UL, actual: PresentPacingControl.FrameDeltaOf(simulates: true, steppedTicks: 1680UL, tickLocked: true, wallTicks: 840UL));
        Assert.Equal(expected: 0UL, actual: PresentPacingControl.FrameDeltaOf(simulates: true, steppedTicks: 0UL, tickLocked: true, wallTicks: 840UL));
        Assert.Equal(expected: 840UL, actual: PresentPacingControl.FrameDeltaOf(simulates: false, steppedTicks: 0UL, tickLocked: true, wallTicks: 840UL));
        Assert.Equal(expected: 840UL, actual: PresentPacingControl.FrameDeltaOf(simulates: true, steppedTicks: 1680UL, tickLocked: false, wallTicks: 840UL));
    }
}
