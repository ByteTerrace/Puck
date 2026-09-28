using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Sources;
using Puck.Commands;

namespace Puck.Hosting.Tests;

// Sources: external instances a producer supplies (source.<producer id>), scheduled by demand at their producer's cadence
// and negotiated extent. Two screens on one camera publish it once a frame, a source nothing visible reads publishes
// nothing, a static source publishes once, a tick source once per completed tick, and a rate source never exceeds its
// rate over a fixed frame sequence.
public sealed partial class RenderGraphSchedulerLawTests {
    private const int CameraHeight = 480;
    private const int CameraWidth = 640;

    // The display shows main alone; one array, so a steady frame described over it allocates nothing.
    private static readonly RenderGraphRoot[] MainRoot = [Full(instance: "main")];

    private static RenderGraphInstance Screens(string name, params string[] producers) => Instance(
        name: name,
        reads: [.. producers.Select(selector: static producer => new RenderGraphRead(Producer: producer))]
    );
    private static RenderGraphSourceState Declared(string instance, ImageSourceCadence cadence, int width = CameraWidth, int height = CameraHeight) => new(
        Cadence: cadence,
        Height: height,
        Instance: instance,
        Width: width
    );
    private static RenderGraphFrame SourceFrame(long index, RenderGraphFootprint[] footprints, RenderGraphSourceState[] sources, long tick = 0, int hertz = 60) => Frame(
        footprints: footprints,
        hertz: hertz,
        index: index,
        roots: MainRoot
    ) with {
        Sources = sources,
        Tick = tick,
    };
    private static RenderGraphFootprint Shows(string consumer, string producer, double fraction = 0.25) => new(
        Consumer: consumer,
        Height: fraction,
        Producer: producer,
        Width: fraction
    );

    [Fact]
    public void TwoScreensOnOneCameraPublishItOnceAFrameCounted() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            Screens("main", "camera", "pane"),
            Screens("pane", "camera")
        );
        RenderGraphSourceState[] sources = [Declared(cadence: ImageSourceCadence.Rate(rateHz: 60U), instance: "camera")];
        var schedules = Run(
            count: 30,
            frame: index => SourceFrame(
                footprints: [
                    Shows(consumer: "main", fraction: 0.2, producer: "camera"),
                    Shows(consumer: "main", fraction: 0.1, producer: "camera"),
                    Shows(consumer: "main", fraction: 0.5, producer: "pane"),
                    Shows(consumer: "pane", fraction: 0.9, producer: "camera"),
                ],
                index: index,
                sources: sources
            ),
            set: set
        );

        // Counted: three screens across two consumers read the camera, and it publishes exactly once a frame.
        Assert.Equal(
            expected: 30,
            actual: RenderCount(
                name: "camera",
                schedules: schedules,
                set: set
            )
        );

        foreach (var schedule in schedules) {
            var camera = Row(name: "camera", schedule: schedule, set: set);

            Assert.Equal(expected: set.IndexOf(name: "camera"), actual: schedule.Renders[0]);
            Assert.Single(collection: schedule.Renders, predicate: index => (index == set.IndexOf(name: "camera")));

            // The negotiated extent, never a footprint's.
            Assert.Equal(expected: (CameraWidth, CameraHeight), actual: (camera.Width, camera.Height));
            Assert.Equal(expected: (((long)CameraWidth) * CameraHeight), actual: camera.PassPixels);
        }

        Assert.Equal(expected: SourceHandle.Producer(name: "camera"), actual: set.Instances[set.IndexOf(name: "camera")].Handle);
        Assert.Equal(expected: SourceHandle.Instance(name: "pane"), actual: set.Instances[set.IndexOf(name: "pane")].Handle);
    }
    [Fact]
    public void ASourceNoVisibleConsumerReadsPublishesNothing() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            Screens("main", "pane"),
            Screens("pane", "camera")
        );
        RenderGraphSourceState[] sources = [Declared(cadence: ImageSourceCadence.Tick, instance: "camera")];

        // The pane that shows the camera is itself off view, and main shows it at zero size.
        var unread = Run(
            count: 12,
            frame: index => SourceFrame(
                footprints: [
                    Shows(consumer: "pane", producer: "camera"),
                    Shows(consumer: "main", fraction: 0, producer: "pane"),
                ],
                index: index,
                sources: sources,
                tick: index
            ),
            set: set
        );

        Assert.Equal(expected: 0, actual: RenderCount(name: "camera", schedules: unread, set: set));
        Assert.All(
            action: schedule => Assert.Equal(expected: RenderGraphInstanceStatus.Unread, actual: Row(name: "camera", schedule: schedule, set: set).Status),
            collection: unread
        );

        // The control: once the pane is on view, the camera publishes on every tick.
        var read = Run(
            count: 12,
            frame: index => SourceFrame(
                footprints: [
                    Shows(consumer: "pane", producer: "camera"),
                    Shows(consumer: "main", producer: "pane"),
                ],
                index: index,
                sources: sources,
                tick: index
            ),
            set: set
        );

        Assert.Equal(expected: 12, actual: RenderCount(name: "camera", schedules: read, set: set));
    }
    [Fact]
    public void AStaticSourcePublishesOnce() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "qr",
                producer: "qr"
            ),
            Screens("main", "qr")
        );
        var schedules = Run(
            count: 40,
            frame: index => SourceFrame(
                footprints: [Shows(consumer: "main", producer: "qr")],
                index: index,
                sources: [Declared(cadence: ImageSourceCadence.Static, height: 29, instance: "qr", width: 29)],
                tick: index
            ),
            set: set
        );

        Assert.Equal(expected: 1, actual: RenderCount(name: "qr", schedules: schedules, set: set));
        Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: Row(name: "qr", schedule: schedules[0], set: set).Status);

        // Every later frame reads the one image it published on frame 0.
        foreach (var schedule in schedules.Skip(count: 1)) {
            Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: Row(name: "qr", schedule: schedule, set: set).Status);
            Assert.Equal(expected: 0L, actual: Assert.Single(collection: schedule.Reads, predicate: static read => (read.Producer == "qr")).Frame);
        }
    }
    [Fact]
    public void ATickSourcePublishesOncePerCompletedTick() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "machine",
                producer: "machine"
            ),
            Screens("main", "machine")
        );
        // Several frames present one tick, and one frame may present several ticks at once.
        long[] ticks = [0, 0, 0, 1, 1, 3, 3, 3, 4, 7, 7];
        var schedules = Run(
            count: ticks.Length,
            frame: index => SourceFrame(
                footprints: [Shows(consumer: "main", producer: "machine")],
                index: index,
                sources: [Declared(cadence: ImageSourceCadence.Tick, height: 144, instance: "machine", width: 160)],
                tick: ticks[index]
            ),
            set: set
        );

        Assert.Equal(
            expected: [0L, 3L, 5L, 8L, 9L],
            actual: schedules.Where(predicate: schedule => (Row(name: "machine", schedule: schedule, set: set).Status == RenderGraphInstanceStatus.Rendered)).Select(selector: static schedule => schedule.Frame)
        );
    }
    [Fact]
    public void ARateSourceNeverExceedsItsRateOverAFixedFrameSequence() {
        const int DisplayHertz = 60;
        const int Seconds = 10;

        foreach (var rate in ((uint[])[1U, 7U, 24U, 25U, 30U, 59U, 60U, 144U])) {
            var set = Set(
                RenderGraphInstance.Source(
                    name: "capture",
                    producer: "capture"
                ),
                Screens("main", "capture")
            );
            var schedules = Run(
                count: (DisplayHertz * Seconds),
                frame: index => SourceFrame(
                    footprints: [Shows(consumer: "main", producer: "capture")],
                    hertz: DisplayHertz,
                    index: index,
                    sources: [Declared(cadence: ImageSourceCadence.Rate(rateHz: rate), instance: "capture")]
                ),
                set: set
            );
            var rendered = schedules.Select(selector: schedule => (Row(name: "capture", schedule: schedule, set: set).Status == RenderGraphInstanceStatus.Rendered)).ToArray();

            // Over every second's worth of consecutive frames, never more than the rate, and at least one image a second.
            for (var start = 0; ((start + DisplayHertz) <= rendered.Length); start++) {
                var count = rendered.Skip(count: start).Take(count: DisplayHertz).Count(predicate: static render => render);

                Assert.InRange(
                    actual: count,
                    high: Math.Min(val1: ((int)rate), val2: DisplayHertz),
                    low: 1
                );
            }
        }
    }
    [Fact]
    public void ASourceWhoseProducerDeclaresNoExtentIsNotRenderedUntilItNegotiatesOne() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            Screens("main", "camera")
        );
        RenderGraphFootprint[] footprints = [Shows(consumer: "main", producer: "camera")];
        var cadence = ImageSourceCadence.Rate(rateHz: 30U);
        var history = RenderGraphHistory.Empty(set: set);
        var frames = new[] {
            SourceFrame(footprints: footprints, index: 0, sources: []),
            SourceFrame(footprints: footprints, index: 1, sources: [Declared(cadence: cadence, height: 0, instance: "camera", width: 0)]),
            SourceFrame(footprints: footprints, index: 2, sources: [Declared(cadence: cadence, height: 720, instance: "camera", width: 1280)]),
        };
        var statuses = new List<(RenderGraphInstanceStatus, int, int)>();

        foreach (var frame in frames) {
            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: frame,
                history: history,
                schedule: schedule,
                set: set
            );
            history = schedule.Next;

            var row = Row(name: "camera", schedule: schedule, set: set);

            statuses.Add(item: (row.Status, row.Width, row.Height));
        }

        Assert.Equal(
            actual: statuses,
            expected: [
                (RenderGraphInstanceStatus.Waiting, 0, 0),
                (RenderGraphInstanceStatus.Waiting, 0, 0),
                (RenderGraphInstanceStatus.Rendered, 1280, 720),
            ]
        );
    }
    [Fact]
    public void ASteadyFrameWithSourcesSchedulesWithoutAllocating() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            RenderGraphInstance.Source(
                name: "machine",
                producer: "machine"
            ),
            Screens("main", "camera", "machine")
        );
        RenderGraphFootprint[] footprints = [Shows(consumer: "main", producer: "camera"), Shows(consumer: "main", producer: "machine")];
        RenderGraphSourceState[] sources = [
            Declared(cadence: ImageSourceCadence.Rate(rateHz: 25U), instance: "camera"),
            Declared(cadence: ImageSourceCadence.Tick, height: 144, instance: "machine", width: 160),
        ];
        RenderGraphSchedule[] schedules = [new(set: set), new(set: set)];
        var history = RenderGraphHistory.Empty(set: set);
        var frame = 0L;

        for (var warm = 0; (warm < 8); warm++) {
            Step();
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var repetition = 0; (repetition < 64); repetition++) {
                Step();
            }
        }));

        void Step() {
            var schedule = schedules[(frame % 2)];

            RenderGraphScheduler.Schedule(
                frame: SourceFrame(
                    footprints: footprints,
                    index: frame,
                    sources: sources,
                    tick: (frame / 2)
                ),
                history: history,
                schedule: schedule,
                set: set
            );
            history = schedule.Next;
            frame++;
        }
    }
    [Fact]
    public void AWithdrawnRenderLeavesAStaticSourceDueAgain() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "qr",
                producer: "qr"
            ),
            Screens("main", "qr")
        );
        var first = new RenderGraphSchedule(set: set);
        var second = new RenderGraphSchedule(set: set);
        var empty = RenderGraphHistory.Empty(set: set);

        RenderGraphFrame FrameOf(long index) => SourceFrame(
            footprints: [Shows(consumer: "main", producer: "qr")],
            index: index,
            sources: [Declared(cadence: ImageSourceCadence.Static, height: 29, instance: "qr", width: 29)]
        );

        RenderGraphScheduler.Schedule(frame: FrameOf(index: 0), history: empty, schedule: first, set: set);
        first.Next.Withdraw(index: set.IndexOf(name: "qr"), previous: empty);
        RenderGraphScheduler.Schedule(frame: FrameOf(index: 1), history: first.Next, schedule: second, set: set);

        Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: Row(name: "qr", schedule: second, set: set).Status);
        Assert.Equal(expected: 1L, actual: second.Next.LatestFrame(index: set.IndexOf(name: "qr")));
    }
    [Fact]
    public void AnIllFormedSourceIsRefusedByName() {
        var settings = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
            ["level"] = JsonSerializer.SerializeToElement(value: 3),
        };

        foreach (var (instance, expected) in (((RenderGraphInstance, string)[])[
            (Instance(name: "view") with { Settings = settings }, "carries settings, but only a source instance"),
            (RenderGraphInstance.Source(name: "camera", producer: "camera") with { ExternalPackage = "source.Camera" }, "names no producer id"),
            (RenderGraphInstance.Source(name: "camera", producer: "camera") with { Refresh = RenderGraphRefresh.Every(divisor: 2) }, "a source is paced by its producer's cadence"),
            (RenderGraphInstance.Source(name: "camera", producer: "camera") with { Output = ShaderPipelineResourceKind.Buffer }, "a source's output is an image"),
        ])) {
            Assert.False(condition: RenderGraphInstanceSet.TryCreate(
                instances: [instance],
                refusal: out var refusal,
                set: out _
            ));
            Assert.Equal(expected: RenderGraphInstanceRefusalCode.SourceDeclaration, actual: refusal.Code);
            Assert.Equal(expected: [instance.Name], actual: refusal.Instances);
            Assert.Contains(expectedSubstring: expected, actualString: refusal.Message);
        }

        // The control: a source carrying settings is well formed.
        _ = Set(RenderGraphInstance.Source(
            name: "pattern",
            producer: "testPattern",
            settings: settings
        ));
    }
    [Fact]
    public void AFrameDeclaringAnythingButASourceOnceIsRefused() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            Screens("main", "camera")
        );
        var tick = ImageSourceCadence.Tick;

        foreach (var sources in ((RenderGraphSourceState[][])[
            [Declared(cadence: tick, instance: "main")],
            [Declared(cadence: tick, instance: "absent")],
            [Declared(cadence: tick, instance: "camera"), Declared(cadence: tick, instance: "camera")],
            [Declared(cadence: tick, height: -1, instance: "camera")],
            [Declared(cadence: new ImageSourceCadence(RateHz: 30U, Refresh: ImageRefresh.Tick), instance: "camera")],
            [Declared(cadence: new ImageSourceCadence(Refresh: ImageRefresh.Rate), instance: "camera")],
        ])) {
            var schedule = new RenderGraphSchedule(set: set);

            Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
                frame: SourceFrame(
                    footprints: [Shows(consumer: "main", producer: "camera")],
                    index: 0,
                    sources: sources
                ),
                history: RenderGraphHistory.Empty(set: set),
                schedule: schedule,
                set: set
            ));
            Assert.Equal(expected: -1L, actual: schedule.Frame);
        }
    }
}
