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
    // A floor footprint holds its producer by the frame's presentation time, not its frame count: paced at 141 Hz but
    // presenting 70 frames a second, a 30 Hz floor renders every second frame (35 Hz) where a count of the pacing
    // target's frames would hold five (14 Hz). A held frame's row says the demand held it; a producer the frame names
    // stale is never held.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFloorFootprintHoldsItsProducerByPresentationTime(bool stale) {
        var set = Set(
            Screens("main", "window"),
            Instance(name: "window")
        );
        const ulong Span = (Puck.Maths.FixedTickConversion.TicksPerSecond / 70UL);
        var schedules = Run(
            count: 20,
            frame: index => (Frame(footprints: [(Shows(consumer: "main", producer: "window") with { Refresh = RenderGraphRefresh.AtLeast(hertz: 30) })], hertz: 141, index: index, roots: MainRoot) with {
                FrameTicks = Span,
                PresentedTicks = (((ulong)index) * Span),
                Stale = (stale ? ["window"] : null),
            }),
            set: set
        );

        Assert.Equal(expected: (stale ? 20 : 10), actual: RenderCount(name: "window", schedules: schedules, set: set));
        Assert.Equal(expected: (stale ? 0 : 10), actual: schedules.Count(predicate: schedule => Row(name: "window", schedule: schedule, set: set).Held));
        Assert.Equal(expected: (19UL * Span), actual: schedules[^1].PresentedTicks);
    }
    [Fact]
    public void AStaleNameNamingNoInstanceIsRefused() {
        var set = Set(
            Screens("main", "window"),
            Instance(name: "window")
        );
        var frame = (Frame(index: 0, roots: MainRoot) with { Stale = ["nobody"] });
        var refusal = Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(frame: frame, history: RenderGraphHistory.Empty(set: set), schedule: new RenderGraphSchedule(set: set), set: set));

        Assert.StartsWith(expectedStartString: "Stale 'nobody' names no instance", actualString: refusal.Message);
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
