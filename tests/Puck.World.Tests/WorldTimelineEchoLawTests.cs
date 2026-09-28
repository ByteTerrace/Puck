using Puck.Hosting;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>world.timeline</c>: it echoes each clock's source and span, and a tick clock's period and start
/// in engine ticks with its phase and reading at the authority's completed engine tick.</summary>
public sealed class WorldTimelineEchoLawTests {
    [Fact]
    public void TheEchoReadsEachClockAtTheCompletedTick() {
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [
                new WorldClock(Name: "day", PeriodSeconds: 1200d, SpanSeconds: 86400d, StartSeconds: 21600d),
                new WorldClock(Name: "tide", State: "tide"),
            ]),
        };
        var period = (1200UL * EngineTicks.PerSecond);
        var echo = WorldTimelineCommandModule.Describe(
            definition: definition,
            engineTick: (period / 4UL)
        );

        Assert.Contains(actualString: echo, expectedSubstring: "clocks=2");
        Assert.Contains(actualString: echo, expectedSubstring: $"periodTicks={period}");
        Assert.Contains(actualString: echo, expectedSubstring: $"startTicks={(period / 4UL)}");
        // A quarter period past a start six hours into the day reads noon.
        Assert.Contains(actualString: echo, expectedSubstring: "phase=0.5");
        Assert.Contains(actualString: echo, expectedSubstring: "reading=43200");
        Assert.Contains(actualString: echo, expectedSubstring: "state=tide");
        // Red leg: an unauthored timeline echoes no clock.
        Assert.Contains(
            expectedSubstring: "clocks=0",
            actualString: WorldTimelineCommandModule.Describe(
                definition: Fixtures.BuildDocument(),
                engineTick: 0UL
            )
        );
    }
}
