using Puck.Abstractions.Sources;
using Puck.Maths;

namespace Puck.Hosting.Tests;

// A floor footprint's lead (RenderGraphRefresh.LeadSeconds, PresentationPhases): the rated views and screens that take a
// tier together render on alternate halves of its period, not all on one frame of it, and none of them on a frame that
// runs a simulation step.
public sealed partial class RenderGraphSchedulerLawTests {
    private const ulong SixtiethTicks = (FixedTickConversion.TicksPerSecond / 60UL);

    // Schedules a 60 Hz display whose even frames run a 30 Hz step, with two views and four tick-cadence screens the
    // display shows through 15 Hz floors. As the host does, each producer takes its lead from its lever's phases once
    // (when spread) and keeps it until the render it asks for. Returns the frames each producer rendered on.
    private static Dictionary<string, List<long>> RatedRenders(bool spread, long count) {
        string[] views = ["view0", "view1"];
        string[] screens = ["screen0", "screen1", "screen2", "screen3"];
        var set = Set([
            Screens("main", [.. views, .. screens]),
            .. views.Select(selector: static name => Instance(name: name)),
            .. screens.Select(selector: static name => RenderGraphInstance.Source(name: name, producer: name)),
        ]);
        RenderGraphSourceState[] sources = [.. screens.Select(selector: static name => Declared(cadence: ImageSourceCadence.Tick, instance: name))];
        var renders = new Dictionary<string, List<long>>();
        var leads = new Dictionary<string, double>();
        var history = RenderGraphHistory.Empty(set: set);
        var (viewPhases, screenPhases) = (new PresentationPhases(), new PresentationPhases());
        var rate = new PresentationRate(DisplayHertz: 60, GridHertz: 60d);

        foreach (var name in views.Concat(second: screens)) {
            renders[name] = [];
        }

        for (var index = 0L; (index < count); index++) {
            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: (SourceFrame(
                    footprints: [.. views.Concat(second: screens).Select(selector: name => (Shows(consumer: "main", producer: name) with {
                        Refresh = RenderGraphRefresh.AtLeast(hertz: 15, leadSeconds: leads.GetValueOrDefault(key: name)),
                    }))],
                    index: index,
                    sources: sources,
                    tick: (index / 2L)
                ) with {
                    FrameTicks = SixtiethTicks,
                    GridHertz = 60d,
                    PresentedTicks = (((ulong)index) * SixtiethTicks),
                    Steps = ((index % 2L) == 0L),
                    StepsNext = ((index % 2L) != 0L),
                }),
                history: history,
                schedule: schedule,
                set: set
            );
            history = schedule.Next;

            foreach (var name in views.Concat(second: screens)) {
                var row = Row(name: name, schedule: schedule, set: set);

                if (row.Status == RenderGraphInstanceStatus.Rendered) {
                    renders[name].Add(item: index);
                }
                // A lead a render could not follow, a screen with no new tick to show, stands until one does.
                if (row.Led) {
                    leads[name] = 0d;
                }
                // Each producer takes the tier on the frame after its first render.
                if (spread && (index == 0L)) {
                    leads[name] = (views.Contains(value: name) ? viewPhases : screenPhases).Take(hertz: 15, rate: rate);
                }
            }
        }

        return renders;
    }
    // The most rated producers any one frame rendered once every lead has placed its producer.
    private static int LargestBurst(Dictionary<string, List<long>> renders) => renders.Values
        .SelectMany(selector: static frames => frames.Where(predicate: static frame => (frame > 4L)))
        .GroupBy(keySelector: static frame => frame)
        .Max(selector: static group => group.Count());

    [Fact]
    public void RatedProducersThatTakeATierTogetherRenderOnAlternateHalvesOfItsPeriod() {
        var together = RatedRenders(count: 40L, spread: false);
        var spread = RatedRenders(count: 40L, spread: true);

        foreach (var (name, frames) in spread) {
            TestContext.Current.TestOutputHelper?.WriteLine(message: $"{name}: one phase [{string.Join(separator: ' ', values: together[name])}] spread [{string.Join(separator: ' ', values: frames)}]");
        }

        // Without a lead every producer renders on the same frame of each period: a burst of six.
        Assert.Equal(expected: 6, actual: LargestBurst(renders: together));
        // With one, each lever's producers split evenly: one view and two screens a frame.
        Assert.Equal(expected: 3, actual: LargestBurst(renders: spread));
        Assert.All(collection: spread, action: static pair => {
            var later = pair.Value.Where(predicate: static frame => (frame > 0L)).ToArray();

            // No rated render lands on a frame that runs a step, no interval passes the floor's four frames, and each
            // producer keeps that period once its lead has placed it.
            Assert.All(collection: later, action: static frame => Assert.Equal(expected: 1L, actual: (frame % 2L)));
            Assert.All(collection: pair.Value.Zip(second: pair.Value.Skip(count: 1)), action: static step => Assert.InRange(actual: (step.Second - step.First), high: 4L, low: 1L));
            Assert.All(collection: later.Zip(second: later.Skip(count: 1)).Skip(count: 2), action: static step => Assert.Equal(expected: 4L, actual: (step.Second - step.First)));
        });
        Assert.NotEqual(expected: (spread["view0"][^1] % 4L), actual: (spread["view1"][^1] % 4L));
        Assert.NotEqual(expected: (spread["screen0"][^1] % 4L), actual: (spread["screen1"][^1] % 4L));
    }
}
