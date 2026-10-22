using Puck.Abstractions.Sources;

namespace Puck.Hosting.Tests;

// A footprint's refresh (RenderGraphFootprint.Refresh): a consumer that renders every frame asks for a producer it shows
// through a rated footprint no more often than the footprint's divisor, a graph instance and a tick source alike, and the
// most frequent of a producer's footprints wins.
public sealed partial class RenderGraphSchedulerLawTests {
    private static RenderGraphFootprint Rated(string consumer, string producer, int divisor) => (Shows(consumer: consumer, producer: producer) with {
        Refresh = RenderGraphRefresh.Every(divisor: divisor),
    });

    [Fact]
    public void ARatedFootprintRendersItsProducerEveryDivisorFrames() {
        var set = Set(
            Screens("main", "window"),
            Instance(name: "window")
        );
        var schedules = Run(
            count: 30,
            frame: index => Frame(footprints: [Rated(consumer: "main", divisor: 2, producer: "window")], hertz: 60, index: index, roots: MainRoot),
            set: set
        );

        Assert.Equal(expected: 30, actual: RenderCount(name: "main", schedules: schedules, set: set));
        Assert.Equal(expected: 15, actual: RenderCount(name: "window", schedules: schedules, set: set));
    }
    [Fact]
    public void ATickSourceThroughARatedFootprintUploadsEveryDivisorFrames() {
        var set = Set(
            RenderGraphInstance.Source(name: "machine", producer: "machine"),
            Screens("main", "machine")
        );
        RenderGraphSourceState[] sources = [Declared(cadence: ImageSourceCadence.Tick, instance: "machine")];
        var schedules = Run(
            count: 32,
            frame: index => SourceFrame(footprints: [Rated(consumer: "main", divisor: 4, producer: "machine")], index: index, sources: sources, tick: index),
            set: set
        );

        Assert.Equal(expected: 8, actual: RenderCount(name: "machine", schedules: schedules, set: set));
    }
    [Fact]
    public void TheMostFrequentFootprintOfAProducerWins() {
        var set = Set(
            Screens("main", "window", "pane"),
            Screens("pane", "window"),
            Instance(name: "window")
        );
        var schedules = Run(
            count: 12,
            frame: index => Frame(
                footprints: [
                    Rated(consumer: "main", divisor: 4, producer: "window"),
                    Shows(consumer: "main", producer: "pane"),
                    Shows(consumer: "pane", producer: "window"),
                    Rated(consumer: "main", divisor: 3, producer: "window"),
                ],
                hertz: 60,
                index: index,
                roots: MainRoot
            ),
            set: set
        );

        Assert.Equal(expected: 12, actual: RenderCount(name: "window", schedules: schedules, set: set));
    }
    [Fact]
    public void AFootprintRefreshNamingNoRateIsRefused() {
        var set = Set(
            Screens("main", "window"),
            Instance(name: "window")
        );
        var schedule = new RenderGraphSchedule(set: set);
        var frame = Frame(footprints: [(Shows(consumer: "main", producer: "window") with { Refresh = new RenderGraphRefresh(Divisor: 0, Hertz: 0) })], index: 0, roots: MainRoot);

        _ = Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(frame: frame, history: RenderGraphHistory.Empty(set: set), schedule: schedule, set: set));
    }
}
