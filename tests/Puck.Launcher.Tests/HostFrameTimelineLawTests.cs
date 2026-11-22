using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The windowed host's <see cref="HostFrameTimeline"/> and its <c>host.frames</c> verb over a clock that moves only when
/// the test advances it: each iteration's interval is the time between its start and the next one's, its phases are
/// the marks it charged, and a reading lists the slowest iterations first.
/// </summary>
public sealed class HostFrameTimelineLawTests {
    [Fact]
    public void AReadingSummarizesTheIntervalsAndListsTheSlowestIterationWithItsPhases() {
        var clock = new StepClock();
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(implementationInstance: clock);
        services.AddLauncherTerminal();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CommandRegistry>();
        var timeline = provider.GetRequiredService<HostFrameTimeline>();

        Assert.Contains(expectedSubstring: "no iterations recorded", actualString: registry.Submit(line: "host.frames").Output);

        // Three iterations: 10 ms of work then a 6.7 ms wait, 20 ms of work with a wait that slept 1 ms late, and 5 ms of
        // work with a wait that slept 3 ms late, the slowest interval though not the most work.
        Iteration(clock: clock, late: 0L, produce: 10_000L, steps: 1, timeline: timeline, wait: 6_700L);
        Iteration(clock: clock, late: 1_000L, produce: 20_000L, steps: 2, timeline: timeline, wait: 1_000L);
        Iteration(clock: clock, late: 3_000L, produce: 5_000L, steps: 0, timeline: timeline, wait: 20_000L);
        timeline.BeginIteration();

        var reading = registry.Submit(line: "host.frames 3 16.7 1");

        Assert.False(condition: reading.IsError);
        Assert.Contains(expectedSubstring: "3 iterations | avg=20.90 p50=21.00 p99=24.92 max=25.00 ms | over 16.7 ms: 1 | busiest 20.00 ms of work | pacer late at most 3.00 ms", actualString: reading.Output);
        Assert.Contains(expectedSubstring: "\n  produce: avg=11.67 p50=10.00 p99=19.80 max=20.00 ms\n  produce-work: avg=11.67 p50=10.00 p99=19.80 max=20.00 ms\n  produce-work parts mean [ms] pump 0 ", actualString: reading.Output);
        // No part was measured, so produce's whole mean is unnamed, and the tick's parts are the two stepped iterations'.
        Assert.Contains(expectedSubstring: " flush 0 other 11.667\n  tick parts mean [ms] over 2 stepped iterations ingress 0 ", actualString: reading.Output);
        Assert.Contains(expectedSubstring: " replay 0 sum 0\n  produce-fence: avg=0.00 p50=0.00 p99=0.00 max=0.00 ms\n  control-gap: avg=12.57 p50=16.70 p99=20.91 max=21.00 ms\n  simulate: avg=0.00 p50=0.00 p99=0.00 max=0.00 ms\n  produce+simulate: avg=11.67 p50=10.00 p99=19.80 max=20.00 ms", actualString: reading.Output);
        Assert.Contains(expectedSubstring: "#2 25.00 ms: input=0.00 commands=0.00 simulate=0.00 (0 steps) begin=0.00 produce=5.00 (fence 0.00) present=0.00 wait=20.00 (late 3.00)", actualString: reading.Output);
        Assert.Contains(expectedSubstring: "#1 21.00 ms (busiest): input=0.00 commands=0.00 simulate=0.00 (2 steps) begin=0.00 produce=20.00 (fence 0.00) present=0.00 wait=1.00 (late 1.00)", actualString: reading.Output);
        Assert.DoesNotContain(expectedSubstring: "#0 ", actualString: reading.Output);

        Assert.Equal(expected: "[host.frames: reset]", actual: registry.Submit(line: "host.frames reset").Output);
        Assert.Contains(expectedSubstring: "no iterations recorded", actualString: registry.Submit(line: "host.frames").Output);
        Assert.True(condition: registry.Submit(line: "host.frames 0").IsError);
        Assert.True(condition: registry.Submit(line: "host.frames 10 -1").IsError);
        Assert.True(condition: registry.Submit(line: "host.frames 10 16.7 1 2").IsError);
    }

    [Fact]
    public void ARecordingsStatisticsAgreeWithValuesComputedByHandOnFixedIntervals() {
        var clock = new StepClock();
        var timeline = new HostFrameTimeline(time: clock);
        var recording = new HostFrameRecording(capacity: 64);

        // Ten iterations whose work is 1 ms, 2 ms ... 10 ms and which wait for nothing, so each interval is its work.
        timeline.StartRecording(recording: recording);
        for (var milliseconds = 1; (milliseconds <= 10); milliseconds++) {
            Iteration(clock: clock, late: 0L, produce: (milliseconds * 1_000L), steps: 1, timeline: timeline, wait: 0L);
        }
        timeline.BeginIteration();

        var statistics = recording.Statistics(from: 0, to: recording.Count, budgetMilliseconds: 5.0);

        Assert.Equal(expected: 10, actual: statistics.Frames);
        Assert.Equal(expected: 0.055, actual: statistics.Seconds, precision: 9);
        Assert.Equal(expected: 5.5, actual: statistics.Interval.Mean, precision: 9);
        Assert.Equal(expected: 5.5, actual: statistics.Interval.P50, precision: 9);
        Assert.Equal(expected: 9.55, actual: statistics.Interval.P95, precision: 9);
        Assert.Equal(expected: 9.91, actual: statistics.Interval.P99, precision: 9);
        Assert.Equal(expected: 9.991, actual: statistics.Interval.P999, precision: 9);
        Assert.Equal(expected: 10.0, actual: statistics.Interval.Max, precision: 9);
        Assert.Equal(expected: statistics.Interval.P99, actual: statistics.Work.P99, precision: 9);
        Assert.Equal(expected: 5, actual: statistics.Over);
        Assert.Equal(expected: 0, actual: statistics.OverTwice);
        Assert.Equal(expected: 9, actual: statistics.WorstIndex);
        Assert.Equal(expected: 1.0, actual: statistics.PacingStep, precision: 9);
        Assert.Equal(expected: Math.Sqrt(d: 8.25), actual: statistics.PacingDeviation, precision: 9);
        Assert.Null(@object: statistics.Low1);
        Assert.Equal(expected: 10L, actual: statistics.Steps);
    }

    private static void Iteration(StepClock clock, HostFrameTimeline timeline, long produce, long wait, long late, int steps) {
        timeline.BeginIteration();
        timeline.Mark(phase: HostFramePhase.Input);
        timeline.Mark(phase: HostFramePhase.Commands);
        timeline.NoteControlPhase();
        timeline.NoteSteps(steps: steps);
        timeline.Mark(phase: HostFramePhase.Simulate);
        timeline.Mark(phase: HostFramePhase.Begin);
        clock.Advance(microseconds: produce);
        timeline.Mark(phase: HostFramePhase.Produce);
        timeline.Mark(phase: HostFramePhase.Present);
        clock.Advance(microseconds: wait);
        if (late > 0L) {
            timeline.NoteLate(ticks: late);
        }
        timeline.Mark(phase: HostFramePhase.Wait);
    }

    // A microsecond clock that moves only when advanced.
    private sealed class StepClock : TimeProvider {
        private long m_now = 1L;

        public override long TimestampFrequency => 1_000_000L;

        public void Advance(long microseconds) => m_now += microseconds;
        public override long GetTimestamp() => m_now;
    }
}
