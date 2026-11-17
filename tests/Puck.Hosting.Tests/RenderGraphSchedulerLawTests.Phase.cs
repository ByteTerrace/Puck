using Puck.Abstractions.Sources;
using Puck.Maths;

namespace Puck.Hosting.Tests;

// A floor footprint's lead (RenderGraphRefresh.LeadSeconds, PresentationPhases): the rated views and screens that take a
// tier together render on alternate halves of its period, not all on one frame of it, and none of them on a frame that
// runs a simulation step.
public sealed partial class RenderGraphSchedulerLawTests {
    private const ulong SixtiethTicks = (FixedTickConversion.TicksPerSecond / 60UL);

    private const ulong SimulationStepTicks = (FixedTickConversion.TicksPerSecond / 30UL);

    // The frame spans, in ticks, of a display that shows frames at any time: a steady rate with the jitter a timer-paced
    // variable-refresh display's frames carry, cycling through offsets that sum to zero.
    private static ulong[] IrregularSpans(ulong baseTicks, long scale = 1L) {
        long[] offsets = [-20L, 55L, -5L, 38L, -42L, 12L, 70L, -63L, 25L, -35L, 8L, -43L];

        return [.. offsets.Select(selector: offset => ((ulong)(((long)baseTicks) + ((offset * scale * ((long)baseTicks)) / 840L))))];
    }
    // Schedules a display whose frames last the cycle of spans (grid: the rate they land a whole number of steps of, or
    // zero off a grid) and run a 30 Hz simulation step whenever the presented time passes one, with two views and a number of
    // tick-cadence screens the display shows through 15 Hz floors. As the host does, each producer takes its lead from its
    // lever's phases once (when spread) and keeps it until the render it asks for. Returns the frames each producer
    // rendered on.
    private static Dictionary<string, List<long>> RatedRenders(bool spread, long count, int displayHertz = 60, double gridHertz = 60d, ulong[]? spans = null, int screenCount = 4) {
        string[] views = ["view0", "view1"];
        string[] screens = [.. Enumerable.Range(start: 0, count: screenCount).Select(selector: static index => $"screen{index}")];
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
        var rate = new PresentationRate(DisplayHertz: displayHertz, GridHertz: gridHertz);
        var cycle = (spans ?? [SixtiethTicks]);
        var presented = 0UL;
        var pending = (SimulationStepTicks - cycle[0]);
        var tick = 0L;

        foreach (var name in views.Concat(second: screens)) {
            renders[name] = [];
        }

        for (var index = 0L; (index < count); index++) {
            var span = cycle[(int)(index % cycle.Length)];
            var next = cycle[(int)((index + 1L) % cycle.Length)];

            pending += span;
            presented += span;

            var steps = (pending >= SimulationStepTicks);

            if (steps) {
                pending -= SimulationStepTicks;
                tick++;
            }

            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: (SourceFrame(
                    footprints: [.. views.Concat(second: screens).Select(selector: name => (Shows(consumer: "main", producer: name) with {
                        Refresh = RenderGraphRefresh.AtLeast(hertz: 15, leadSeconds: leads.GetValueOrDefault(key: name)),
                    }))],
                    hertz: displayHertz,
                    index: index,
                    sources: sources,
                    tick: tick
                ) with {
                    FrameTicks = span,
                    GridHertz = gridHertz,
                    PresentedTicks = presented,
                    Steps = steps,
                    StepsNext = ((pending + next) >= SimulationStepTicks),
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
    private static int LargestBurst(Dictionary<string, List<long>> renders, long after = 4L) => renders.Values
        .SelectMany(selector: frames => frames.Where(predicate: frame => (frame > after)))
        .GroupBy(keySelector: static frame => frame)
        .Max(selector: static group => group.Count());

    // On a display with no grid, frames jitter about the pacing cadence: a 15 Hz tier's lead must still split the views, and
    // the screens, over its period for as long as the frames run, where each producer's interval counted in summed spans
    // flips between n and n - 1 frames and walks the producers onto one frame.
    [Theory]
    [InlineData(60, 840UL)]
    [InlineData(144, 350UL)]
    public void RatedProducersSpreadOnADisplayWithNoGrid(int displayHertz, ulong baseTicks) {
        var spans = IrregularSpans(baseTicks: baseTicks, scale: 2L);
        var together = RatedRenders(count: 400L, displayHertz: displayHertz, gridHertz: 0d, screenCount: 8, spans: spans, spread: false);
        var spread = RatedRenders(count: 400L, displayHertz: displayHertz, gridHertz: 0d, screenCount: 8, spans: spans, spread: true);

        TestContext.Current.TestOutputHelper?.WriteLine(message: $"burst together {LargestBurst(renders: together, after: 40L)} spread {LargestBurst(renders: spread, after: 40L)}");

        // Without a lead the two views and eight screens render together; with one, each lever splits evenly.
        Assert.Equal(expected: 10, actual: LargestBurst(renders: together, after: 40L));
        Assert.Equal(expected: 1, actual: LargestBurst(renders: spread.Where(predicate: static pair => pair.Key.StartsWith(value: "view", comparisonType: StringComparison.Ordinal)).ToDictionary(), after: 40L));
        Assert.Equal(expected: 4, actual: LargestBurst(renders: spread.Where(predicate: static pair => pair.Key.StartsWith(value: "screen", comparisonType: StringComparison.Ordinal)).ToDictionary(), after: 40L));
    }
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
