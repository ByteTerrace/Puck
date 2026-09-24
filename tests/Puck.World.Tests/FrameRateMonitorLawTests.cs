using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the frame-rate witness: its window ends at the reading instant, so frames that stop arriving read as a
/// stall instead of a replay of the last samples.
/// </summary>
public sealed class FrameRateMonitorLawTests {
    [Fact]
    public void Summarize_SteadyFrames_ReportsTheirRate() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);

        var (averageFps, worstFps, frameCount) = monitor.Summarize();

        Assert.Equal(actual: averageFps, expected: 50f, tolerance: 0.5f);
        Assert.Equal(actual: worstFps, expected: 50f, tolerance: 0.5f);
        Assert.InRange(actual: frameCount, high: 100, low: 99);
    }
    [Fact]
    public void Summarize_FramesStoppedBriefly_CountsTheOpenGapAsTheSlowestFrame() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);
        clock.Advance(by: TimeSpan.FromSeconds(value: 0.5));

        var (averageFps, worstFps, frameCount) = monitor.Summarize();

        Assert.Equal(actual: worstFps, expected: 2f, tolerance: 0.01f);
        Assert.Equal(actual: averageFps, expected: 37.5f, tolerance: 0.5f);
        Assert.InRange(actual: frameCount, high: 75, low: 74);
    }
    [Fact]
    public void Summarize_FramesStoppedForTheWholeWindow_ReportsNoComposedFrame() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);
        clock.Advance(by: TimeSpan.FromSeconds(value: 4.0));

        var (averageFps, worstFps, frameCount) = monitor.Summarize();

        Assert.Equal(actual: frameCount, expected: 0);
        Assert.Equal(actual: averageFps, expected: 0f);
        Assert.Equal(actual: worstFps, expected: 0.25f, tolerance: 0.001f);
    }
    [Fact]
    public void Summarize_NeverSampled_ReportsNothing() {
        var (averageFps, worstFps, frameCount) = new FrameRateMonitor(timeProvider: new VirtualClock()).Summarize();

        Assert.Equal(actual: frameCount, expected: 0);
        Assert.Equal(actual: averageFps, expected: 0f);
        Assert.Equal(actual: worstFps, expected: 0f);
    }

    private static void Run(VirtualClock clock, FrameRateMonitor monitor, int frames, float deltaSeconds) {
        for (var frame = 0; (frame < frames); frame++) {
            clock.Advance(by: TimeSpan.FromSeconds(value: deltaSeconds));
            monitor.Sample(deltaSeconds: deltaSeconds);
        }
    }
}
