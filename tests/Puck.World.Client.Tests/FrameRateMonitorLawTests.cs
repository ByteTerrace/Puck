using Puck.Testing;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Laws for the frame-rate witness: its window ends at the reading instant, so frames that stop arriving read as a
/// stall instead of a replay of the last samples, and it holds at least four frames, so a renderer composing slowly
/// reads as its rate instead of a stall.
/// </summary>
public sealed class FrameRateMonitorLawTests {
    [Fact]
    public void Summarize_SteadyFrames_ReportsTheirRate() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);

        var summary = monitor.Summarize();

        Assert.Equal(actual: summary.AverageFps, expected: 50f, tolerance: 0.5f);
        Assert.Equal(actual: summary.WorstFps, expected: 50f, tolerance: 0.5f);
        Assert.InRange(actual: summary.FrameCount, high: 100, low: 99);
        Assert.False(condition: summary.Stalled);
    }
    [Fact]
    public void Summarize_FramesStoppedBriefly_CountsTheOpenGapAsTheSlowestFrame() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);
        clock.Advance(by: TimeSpan.FromSeconds(value: 0.5));

        var summary = monitor.Summarize();

        // 75 frames in the 1.5 s the gap leaves, over 1.5 s plus the gap's 0.48 s beyond a typical frame.
        Assert.Equal(actual: summary.WorstFps, expected: 2f, tolerance: 0.01f);
        Assert.Equal(actual: summary.AverageFps, expected: 37.8f, tolerance: 0.2f);
        Assert.InRange(actual: summary.FrameCount, high: 75, low: 74);
        Assert.False(condition: summary.Stalled);
    }
    [Fact]
    public void Summarize_FramesStoppedForTheWholeWindow_ReportsAStall() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        Run(clock: clock, deltaSeconds: 0.02f, frames: 120, monitor: monitor);
        clock.Advance(by: TimeSpan.FromSeconds(value: 4.0));

        var summary = monitor.Summarize();

        Assert.True(condition: summary.Stalled);
        Assert.Equal(actual: summary.StalledSeconds, expected: 4f, tolerance: 0.001f);
        Assert.Equal(actual: summary.AverageFps, expected: 0f);
        Assert.Equal(actual: summary.WorstFps, expected: 0.25f, tolerance: 0.001f);
    }
    [Fact]
    public void Summarize_FramesSlowerThanTheWindow_ReportTheirRate() {
        var clock = new VirtualClock();
        var monitor = new FrameRateMonitor(timeProvider: clock);

        // 0.8 FPS: every frame outlasts half the window, and the next one is 1.2 s along.
        Run(clock: clock, deltaSeconds: 1.25f, frames: 8, monitor: monitor);
        clock.Advance(by: TimeSpan.FromSeconds(value: 1.2));

        var slow = monitor.Summarize();

        Assert.False(condition: slow.Stalled);
        Assert.Equal(actual: slow.FrameCount, expected: 4);
        Assert.Equal(actual: slow.AverageFps, expected: 0.8f, tolerance: 0.001f);
        Assert.Equal(actual: slow.WorstFps, expected: 0.8f, tolerance: 0.001f);

        // Five frames' time with nothing composed is a stall at this rate too.
        clock.Advance(by: TimeSpan.FromSeconds(value: 4.0));

        var stalled = monitor.Summarize();

        Assert.True(condition: stalled.Stalled);
        Assert.Equal(actual: stalled.StalledSeconds, expected: 5.2f, tolerance: 0.001f);
    }
    [Fact]
    public void Summarize_NeverSampled_ReportsNothing() {
        var summary = new FrameRateMonitor(timeProvider: new VirtualClock()).Summarize();

        Assert.Equal(actual: summary.FrameCount, expected: 0);
        Assert.Equal(actual: summary.AverageFps, expected: 0f);
        Assert.Equal(actual: summary.WorstFps, expected: 0f);
        Assert.False(condition: summary.Stalled);
    }

    private static void Run(VirtualClock clock, FrameRateMonitor monitor, int frames, float deltaSeconds) {
        for (var frame = 0; (frame < frames); frame++) {
            clock.Advance(by: TimeSpan.FromSeconds(value: deltaSeconds));
            monitor.Sample(deltaSeconds: deltaSeconds);
        }
    }
}
