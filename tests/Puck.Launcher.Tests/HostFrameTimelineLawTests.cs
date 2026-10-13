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
        Assert.Contains(expectedSubstring: "3 iterations | avg=20.90 p50=21.00 p99=25.00 max=25.00 ms | over 16.7 ms: 2 | busiest 20.00 ms of work | pacer late at most 3.00 ms", actualString: reading.Output);
        Assert.Contains(expectedSubstring: "#2 25.00 ms: input=0.00 commands=0.00 simulate=0.00 (0 steps) begin=0.00 produce=5.00 present=0.00 wait=20.00 (late 3.00)", actualString: reading.Output);
        Assert.Contains(expectedSubstring: "#1 21.00 ms (busiest): input=0.00 commands=0.00 simulate=0.00 (2 steps) begin=0.00 produce=20.00 present=0.00 wait=1.00 (late 1.00)", actualString: reading.Output);
        Assert.DoesNotContain(expectedSubstring: "#0 ", actualString: reading.Output);

        Assert.Equal(expected: "[host.frames: reset]", actual: registry.Submit(line: "host.frames reset").Output);
        Assert.Contains(expectedSubstring: "no iterations recorded", actualString: registry.Submit(line: "host.frames").Output);
        Assert.True(condition: registry.Submit(line: "host.frames 0").IsError);
        Assert.True(condition: registry.Submit(line: "host.frames 10 -1").IsError);
        Assert.True(condition: registry.Submit(line: "host.frames 10 16.7 1 2").IsError);
    }

    private static void Iteration(StepClock clock, HostFrameTimeline timeline, long produce, long wait, long late, int steps) {
        timeline.BeginIteration();
        timeline.Mark(phase: HostFramePhase.Input);
        timeline.Mark(phase: HostFramePhase.Commands);
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
