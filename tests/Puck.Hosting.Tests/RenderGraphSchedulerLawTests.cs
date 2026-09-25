using Puck.Abstractions.Counting;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for the render-graph demand scheduler: an instance renders only when something rendering shows it and at most
/// once a frame, at the extent its footprint needs, at its declared refresh with consumers reading its latest completed
/// output, and within the policy's pass-pixel budget; a view that sees itself reads its previous frame, a loop of
/// same-frame reads is refused naming every instance in it, a refused frame leaves its schedule unchanged, and a steady
/// frame scheduled into two alternating schedules allocates nothing and matches a run given fresh ones. A buffer read
/// renders its producer once a frame before the views that read it, at no extent and for no pass-pixels, and a read of
/// another kind than its producer's output is refused naming both instances.
/// </summary>
public sealed class RenderGraphSchedulerLawTests {
    private const int DisplayHeight = 1080;
    private const int DisplayWidth = 1920;

    private static RenderGraphInstance Instance(string name, int passes = 1, RenderGraphRefresh? refresh = null, RenderGraphRead[]? reads = null, ShaderPipelineResourceKind output = ShaderPipelineResourceKind.Image) => new(
        Name: name,
        Output: output,
        Passes: passes,
        Reads: (reads ?? []),
        Refresh: (refresh ?? RenderGraphRefresh.EveryFrame)
    );
    // The world's brick pool: world-scoped work whose output is a buffer the views read.
    private static RenderGraphInstance Bricks(RenderGraphRead[]? reads = null) => Instance(
        name: "bricks",
        output: ShaderPipelineResourceKind.Buffer,
        passes: 2,
        reads: reads
    );

    // The SDF engine's pass count (SdfWorldEngine.PassLabels.Length), which prices the sdf.world producer.
    private const int WorldPasses = 10;

    // The sdf.world external producer, which renders through the engine's own ring.
    private static RenderGraphInstance World(RenderGraphRead[]? reads = null) => Instance(
        name: "world",
        passes: WorldPasses,
        reads: reads
    ) with {
        ExternalPackage = "sdf.world",
    };
    private static RenderGraphRead BufferRead(string producer, bool previousFrame = false) => new(
        Kind: ShaderPipelineResourceKind.Buffer,
        PreviousFrame: previousFrame,
        Producer: producer
    );
    private static RenderGraphInstanceSet Set(params RenderGraphInstance[] instances) {
        Assert.True(
            condition: RenderGraphInstanceSet.TryCreate(
                instances: instances,
                refusal: out var refusal,
                set: out var set
            ),
            userMessage: refusal?.Message
        );

        return set;
    }
    private static RenderGraphFrame Frame(long index, RenderGraphRoot[] roots, RenderGraphFootprint[]? footprints = null, long budget = 0, int hertz = 0) => new(
        DisplayHeight: DisplayHeight,
        DisplayHertz: hertz,
        DisplayWidth: DisplayWidth,
        Footprints: (footprints ?? []),
        Index: index,
        PassPixelBudget: budget,
        Roots: roots
    );
    private static RenderGraphRoot Full(string instance) => new(
        Height: 1,
        Instance: instance,
        Width: 1
    );
    // Schedules frames 0..count-1 with the same visibility, each into a schedule of its own, and returns every schedule.
    private static List<RenderGraphSchedule> Run(RenderGraphInstanceSet set, long count, Func<long, RenderGraphFrame> frame) {
        var schedules = new List<RenderGraphSchedule>();
        var history = RenderGraphHistory.Empty(set: set);

        for (var index = 0L; (index < count); index++) {
            var schedule = new RenderGraphSchedule(set: set);

            RenderGraphScheduler.Schedule(
                frame: frame(arg: index),
                history: history,
                schedule: schedule,
                set: set
            );
            schedules.Add(item: schedule);
            history = schedule.Next;
        }

        return schedules;
    }
    private static RenderGraphInstanceSchedule Row(RenderGraphSchedule schedule, RenderGraphInstanceSet set, string name) => schedule.Instances[set.IndexOf(name: name)];
    private static int RenderCount(IEnumerable<RenderGraphSchedule> schedules, RenderGraphInstanceSet set, string name) => schedules.Sum(selector: schedule => schedule.Renders.Count(predicate: index => (index == set.IndexOf(name: name))));

    [Fact]
    public void ACameraShownOnTwoScreensRendersOncePerFrame() {
        var set = Set(
            Instance(name: "security"),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "security")]
            ),
            Instance(
                name: "pane",
                reads: [new RenderGraphRead(Producer: "security")]
            )
        );
        var schedules = Run(
            count: 12,
            frame: index => Frame(
                footprints: [
                    new RenderGraphFootprint(Consumer: "main", Height: 0.2, Producer: "security", Width: 0.2),
                    new RenderGraphFootprint(Consumer: "main", Height: 0.1, Producer: "security", Width: 0.1),
                    new RenderGraphFootprint(Consumer: "pane", Height: 0.5, Producer: "security", Width: 0.5),
                ],
                index: index,
                roots: [
                    Full(instance: "main"),
                    new RenderGraphRoot(Height: 0.25, Instance: "pane", Width: 0.25),
                ]
            ),
            set: set
        );

        Assert.Equal(
            expected: 12,
            actual: RenderCount(
                name: "security",
                schedules: schedules,
                set: set
            )
        );

        foreach (var schedule in schedules) {
            var order = schedule.Renders.Select(selector: index => set.Instances[index].Name).ToArray();

            Assert.Equal(
                actual: order,
                expected: ["security", "main", "pane"]
            );
            Assert.All(
                action: read => Assert.Equal(expected: schedule.Frame, actual: read.Frame),
                collection: schedule.Reads
            );
        }

        // The largest footprint decides the extent: the main view's 0.2 over the pane's 0.5 of a quarter, quantized up
        // to 13/64 of each axis.
        Assert.Equal(
            expected: (390, 220),
            actual: (Row(name: "security", schedule: schedules[^1], set: set).Width, Row(name: "security", schedule: schedules[^1], set: set).Height)
        );
    }
    [Fact]
    public void ACameraWhoseOnlyScreenIsOffViewRendersZeroTimes() {
        var set = Set(
            Instance(name: "security"),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "security")]
            )
        );
        var offView = Run(
            count: 8,
            frame: index => Frame(
                footprints: (((index % 2) == 0)
                    ? []
                    : [new RenderGraphFootprint(Consumer: "main", Height: 0, Producer: "security", Width: 0.3)]),
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        );
        var onView = Run(
            count: 8,
            frame: index => Frame(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.3, Producer: "security", Width: 0.3)],
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        );

        Assert.Equal(
            expected: 0,
            actual: RenderCount(
                name: "security",
                schedules: offView,
                set: set
            )
        );
        Assert.All(
            action: schedule => Assert.Equal(expected: RenderGraphInstanceStatus.Unread, actual: Row(name: "security", schedule: schedule, set: set).Status),
            collection: offView
        );
        Assert.Equal(
            expected: 8,
            actual: RenderCount(
                name: "security",
                schedules: onView,
                set: set
            )
        );
    }
    [Fact]
    public void AMirrorFacingItselfReadsThePreviousFrame() {
        var set = Set(Instance(
            name: "mirror",
            reads: [new RenderGraphRead(Producer: "mirror")]
        ));

        Assert.True(condition: set.Reads[0][0].PreviousFrame);

        var schedules = Run(
            count: 4,
            frame: index => Frame(
                footprints: [new RenderGraphFootprint(Consumer: "mirror", Height: 0.4, Producer: "mirror", Width: 0.4)],
                index: index,
                roots: [Full(instance: "mirror")]
            ),
            set: set
        );

        for (var frame = 0; (frame < schedules.Count); frame++) {
            var read = Assert.Single(collection: schedules[frame].Reads);

            Assert.True(condition: read.PreviousFrame);
            Assert.Equal(
                expected: (frame - 1),
                actual: read.Frame
            );
            Assert.Equal(
                expected: [0],
                actual: schedules[frame].Renders
            );
        }
    }
    [Fact]
    public void ASameFrameCycleRefusesNamingEveryInstanceInIt() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Instance(name: "main"),
                Instance(
                    name: "east",
                    reads: [new RenderGraphRead(Producer: "west")]
                ),
                Instance(
                    name: "west",
                    reads: [new RenderGraphRead(Producer: "east")]
                ),
            ],
            refusal: out var refusal,
            set: out _
        ));
        Assert.Equal(
            expected: RenderGraphInstanceRefusalCode.SameFrameCycle,
            actual: refusal.Code
        );
        Assert.Equal(
            expected: ["east", "west"],
            actual: refusal.Instances
        );
        Assert.Contains(
            expectedSubstring: "east -> west -> east",
            actualString: refusal.Message
        );

        // The control: one previous-frame read breaks the loop, and that consumer samples the producer's last frame.
        var set = Set(
            Instance(
                name: "east",
                reads: [new RenderGraphRead(Producer: "west")]
            ),
            Instance(
                name: "west",
                reads: [new RenderGraphRead(PreviousFrame: true, Producer: "east")]
            )
        );
        var schedules = Run(
            count: 3,
            frame: index => Frame(
                footprints: [
                    new RenderGraphFootprint(Consumer: "east", Height: 0.3, Producer: "west", Width: 0.3),
                    new RenderGraphFootprint(Consumer: "west", Height: 0.3, Producer: "east", Width: 0.3),
                ],
                index: index,
                roots: [Full(instance: "east")]
            ),
            set: set
        );
        var last = schedules[^1];

        Assert.Equal(
            expected: ["west", "east"],
            actual: last.Renders.Select(selector: index => set.Instances[index].Name).ToArray()
        );
        Assert.Contains(
            collection: last.Reads,
            expected: new RenderGraphReadSchedule(Consumer: "west", Frame: 1, PreviousFrame: true, Producer: "east")
        );
        Assert.Contains(
            collection: last.Reads,
            expected: new RenderGraphReadSchedule(Consumer: "east", Frame: 2, PreviousFrame: false, Producer: "west")
        );
    }
    [Fact]
    public void AViewOccupyingAQuarterOfTheScreenRendersAtAQuarterOfTheExtent() {
        var set = Set(
            Instance(
                name: "main",
                passes: 3
            ),
            Instance(
                name: "pane",
                passes: 3
            )
        );
        var schedule = Run(
            count: 1,
            frame: index => Frame(
                index: index,
                roots: [
                    Full(instance: "main"),
                    new RenderGraphRoot(Height: 0.5, Instance: "pane", Width: 0.5),
                ]
            ),
            set: set
        )[0];
        var main = Row(name: "main", schedule: schedule, set: set);
        var pane = Row(name: "pane", schedule: schedule, set: set);

        Assert.Equal(expected: (DisplayWidth, DisplayHeight), actual: (main.Width, main.Height));
        Assert.Equal(expected: ((DisplayWidth / 2), (DisplayHeight / 2)), actual: (pane.Width, pane.Height));
        Assert.Equal(expected: (main.PassPixels / 4), actual: pane.PassPixels);
        Assert.Equal(expected: ((((3L * DisplayWidth) * DisplayHeight) * 5) / 4), actual: schedule.PassPixels);
    }
    [Fact]
    public void ANestedViewRendersAtItsFootprintInsideItsConsumer() {
        var set = Set(
            Instance(name: "inner"),
            Instance(
                name: "portal",
                reads: [new RenderGraphRead(Producer: "inner")]
            ),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "portal")]
            )
        );
        var schedule = Run(
            count: 1,
            frame: index => Frame(
                footprints: [
                    new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "portal", Width: 0.5),
                    new RenderGraphFootprint(Consumer: "portal", Height: 0.5, Producer: "inner", Width: 0.5),
                ],
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        )[0];

        Assert.Equal(expected: ((DisplayWidth / 4), (DisplayHeight / 4)), actual: (Row(name: "inner", schedule: schedule, set: set).Width, Row(name: "inner", schedule: schedule, set: set).Height));
        Assert.Equal(expected: ["inner", "portal", "main"], actual: schedule.Renders.Select(selector: index => set.Instances[index].Name).ToArray());
    }
    [Fact]
    public void ADivisorIsHonouredAndConsumersReadTheLatestCompletedOutput() {
        foreach (var refresh in ((RenderGraphRefresh[])[RenderGraphRefresh.Every(divisor: 3), RenderGraphRefresh.At(hertz: 20)])) {
            var set = Set(
                Instance(
                    name: "security",
                    refresh: refresh
                ),
                Instance(
                    name: "main",
                    reads: [new RenderGraphRead(Producer: "security")]
                )
            );
            var schedules = Run(
                count: 9,
                frame: index => Frame(
                    footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "security", Width: 0.25)],
                    hertz: 60,
                    index: index,
                    roots: [Full(instance: "main")]
                ),
                set: set
            );
            var rendered = schedules.Where(predicate: schedule => (Row(name: "security", schedule: schedule, set: set).Status == RenderGraphInstanceStatus.Rendered)).Select(selector: static schedule => schedule.Frame).ToArray();

            Assert.Equal(actual: rendered, expected: [0L, 3L, 6L]);
            Assert.All(
                action: schedule => Assert.Equal(expected: (schedule.Frame - (schedule.Frame % 3)), actual: Assert.Single(collection: schedule.Reads).Frame),
                collection: schedules
            );
            Assert.All(
                action: schedule => Assert.Equal(expected: 3, actual: Row(name: "security", schedule: schedule, set: set).Divisor),
                collection: schedules
            );
        }
    }
    [Fact]
    public void TheBudgetDefersTheFreshestAndStarvesNoInstance() {
        var set = Set(
            Instance(name: "north"),
            Instance(name: "south"),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "north"), new RenderGraphRead(Producer: "south")]
            )
        );
        // Each camera covers a quarter of each axis, so one render costs 480 x 270 pass-pixels: the budget fits one.
        var schedules = Run(
            count: 6,
            frame: index => Frame(
                budget: (480 * 270),
                footprints: [
                    new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "north", Width: 0.25),
                    new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "south", Width: 0.25),
                ],
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        );

        Assert.All(
            action: schedule => Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: Row(name: "main", schedule: schedule, set: set).Status),
            collection: schedules
        );
        Assert.Equal(
            expected: ["north", "south", "north", "south", "north", "south"],
            actual: schedules.Select(selector: schedule => set.Instances[schedule.Renders[0]].Name).ToArray()
        );
        Assert.Equal(
            expected: RenderGraphInstanceStatus.Deferred,
            actual: Row(name: "south", schedule: schedules[0], set: set).Status
        );
        Assert.Contains(
            collection: schedules[1].Reads,
            expected: new RenderGraphReadSchedule(Consumer: "main", Frame: 0, PreviousFrame: false, Producer: "north")
        );
    }
    [Fact]
    public void AnExtentQuantizesUpWithinOneSixteenthOfItsOctaveAndHoldsSmallShrinks() {
        Assert.Equal(expected: 0.5, actual: RenderGraphExtent.Quantize(fraction: 0.5));
        Assert.Equal(expected: 1.0, actual: RenderGraphExtent.Quantize(fraction: 3.0));
        Assert.Equal(expected: 0.0, actual: RenderGraphExtent.Quantize(fraction: 0.0));
        Assert.Equal(expected: 0.3125, actual: RenderGraphExtent.Quantize(fraction: 0.3));

        for (var need = 0.001; (need < 1); need += 0.00731) {
            var quantized = RenderGraphExtent.Quantize(fraction: need);
            var octaveTop = Math.ScaleB(x: 1.0, n: (Math.ILogB(x: need) + 1));

            Assert.InRange(actual: quantized, high: (need + (octaveTop / 16)), low: need);
        }

        Assert.Equal(expected: 0.5, actual: RenderGraphExtent.Quantize(allocated: 0.5, fraction: 0.46));
        Assert.Equal(expected: 0.40625, actual: RenderGraphExtent.Quantize(allocated: 0.5, fraction: 0.4));
        Assert.Equal(expected: 0.5625, actual: RenderGraphExtent.Quantize(allocated: 0.5, fraction: 0.55));
    }
    [Fact]
    public void AWorldScopedBufferRendersOnceBeforeEveryViewThatReadsIt() {
        var set = Set(
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "security"), BufferRead(producer: "bricks")]
            ),
            Instance(
                name: "pane",
                reads: [BufferRead(producer: "bricks")]
            ),
            Instance(
                name: "security",
                reads: [BufferRead(producer: "bricks")]
            ),
            Bricks()
        );
        // A budget of one pass-pixel defers the shown camera; the buffer costs none, so it is never deferred.
        var schedules = Run(
            count: 6,
            frame: index => Frame(
                budget: 1,
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "security", Width: 0.25)],
                index: index,
                roots: [
                    Full(instance: "main"),
                    new RenderGraphRoot(Height: 0.25, Instance: "pane", Width: 0.25),
                ]
            ),
            set: set
        );

        foreach (var schedule in schedules) {
            var bricks = Row(
                name: "bricks",
                schedule: schedule,
                set: set
            );

            Assert.Equal(
                actual: schedule.Renders.Select(selector: index => set.Instances[index].Name).ToArray(),
                expected: ["bricks", "pane", "main"]
            );
            Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: bricks.Status);
            Assert.Equal(expected: (0, 0, 0L), actual: (bricks.Width, bricks.Height, bricks.PassPixels));
            Assert.Equal(expected: RenderGraphInstanceStatus.Deferred, actual: Row(name: "security", schedule: schedule, set: set).Status);
            Assert.Equal(
                actual: schedule.Reads.Where(predicate: static read => (read.Kind == ShaderPipelineResourceKind.Buffer)).ToArray(),
                expected: [
                    new RenderGraphReadSchedule(Consumer: "pane", Frame: schedule.Frame, Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: false, Producer: "bricks"),
                    new RenderGraphReadSchedule(Consumer: "main", Frame: schedule.Frame, Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: false, Producer: "bricks"),
                ]
            );
        }
    }
    [Fact]
    public void ABufferNoRenderingViewReadsIsNotRenderedAndAPreviousFrameReadTakesItsLastOutput() {
        var set = Set(
            Instance(name: "main"),
            Instance(
                name: "security",
                reads: [BufferRead(producer: "bricks")]
            ),
            Instance(
                name: "mirror",
                reads: [BufferRead(previousFrame: true, producer: "bricks")]
            ),
            Bricks()
        );
        var offView = Run(
            count: 3,
            frame: index => Frame(
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        );

        Assert.All(
            action: schedule => Assert.Equal(expected: RenderGraphInstanceStatus.Unread, actual: Row(name: "bricks", schedule: schedule, set: set).Status),
            collection: offView
        );

        var mirrored = Run(
            count: 3,
            frame: index => Frame(
                index: index,
                roots: [Full(instance: "mirror")]
            ),
            set: set
        );

        Assert.Equal(
            expected: new RenderGraphReadSchedule(Consumer: "mirror", Frame: 1, Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: true, Producer: "bricks"),
            actual: Assert.Single(collection: mirrored[^1].Reads)
        );
        Assert.Equal(
            expected: 3,
            actual: RenderCount(
                name: "bricks",
                schedules: mirrored,
                set: set
            )
        );
    }
    [Fact]
    public void AReadOfTheOtherKindIsRefusedNamingBothInstances() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Instance(
                    name: "main",
                    reads: [new RenderGraphRead(Producer: "bricks")]
                ),
                Bricks(),
            ],
            refusal: out var imageOfBuffer,
            set: out _
        ));
        Assert.Equal(expected: RenderGraphInstanceRefusalCode.KindMismatch, actual: imageOfBuffer.Code);
        Assert.Equal(expected: ["main", "bricks"], actual: imageOfBuffer.Instances);
        Assert.Contains(expectedSubstring: "reads 'bricks' as Image, but its output is Buffer", actualString: imageOfBuffer.Message);

        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Instance(
                    name: "main",
                    reads: [BufferRead(producer: "security")]
                ),
                Instance(name: "security"),
            ],
            refusal: out var bufferOfImage,
            set: out _
        ));
        Assert.Equal(expected: RenderGraphInstanceRefusalCode.KindMismatch, actual: bufferOfImage.Code);
        Assert.Equal(expected: ["main", "security"], actual: bufferOfImage.Instances);
        Assert.Contains(expectedSubstring: "reads 'security' as Buffer, but its output is Image", actualString: bufferOfImage.Message);
    }
    [Fact]
    public void AnExternalProducerThatReadsIsRefusedByName() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Instance(name: "camera"),
                World(reads: [new RenderGraphRead(Producer: "camera")]),
            ],
            refusal: out var refusal,
            set: out _
        ));
        Assert.Equal(expected: RenderGraphInstanceRefusalCode.ExternalReads, actual: refusal.Code);
        Assert.Equal(expected: ["world"], actual: refusal.Instances);
        Assert.Contains(expectedSubstring: "'world' is the external producer 'sdf.world'", actualString: refusal.Message);
    }
    [Fact]
    public void APreviousFrameReadOfTheWorldProducerIsRefusedByName() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                World(),
                Instance(
                    name: "main",
                    reads: [new RenderGraphRead(
                        PreviousFrame: true,
                        Producer: "world"
                    )]
                ),
            ],
            refusal: out var refusal,
            set: out _
        ));
        Assert.Equal(expected: RenderGraphInstanceRefusalCode.ExternalPreviousFrame, actual: refusal.Code);
        Assert.Equal(expected: ["main", "world"], actual: refusal.Instances);
        Assert.Contains(expectedSubstring: "reads the previous frame of 'world', the external producer 'sdf.world'", actualString: refusal.Message);
    }
    [Fact]
    public void AnExternalProducerIsScheduledAndPricedLikeAnyInstance() {
        var set = Set(
            World(),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "world")]
            )
        );
        var schedule = new RenderGraphSchedule(set: set);

        RenderGraphScheduler.Schedule(
            frame: Frame(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "world", Width: 0.5)],
                index: 0,
                roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)]
            ),
            history: RenderGraphHistory.Empty(set: set),
            schedule: schedule,
            set: set
        );

        var world = schedule.Instances[set.IndexOf(name: "world")];

        Assert.Equal(expected: [set.IndexOf(name: "world"), set.IndexOf(name: "main")], actual: schedule.Renders);
        Assert.Equal(expected: RenderGraphInstanceKind.External, actual: set.Instances[set.IndexOf(name: "world")].Kind);
        Assert.Equal(
            expected: (WorldPasses, ((((long)WorldPasses) * world.Width) * world.Height)),
            actual: (world.Passes, world.PassPixels)
        );
    }
    [Fact]
    public void ASameFrameBufferCycleRefusesNamingBothInstances() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Bricks(reads: [BufferRead(producer: "lattice")]),
                Instance(
                    name: "lattice",
                    output: ShaderPipelineResourceKind.Buffer,
                    reads: [BufferRead(producer: "bricks")]
                ),
            ],
            refusal: out var refusal,
            set: out _
        ));
        Assert.Equal(expected: RenderGraphInstanceRefusalCode.SameFrameCycle, actual: refusal.Code);
        Assert.Equal(expected: ["bricks", "lattice"], actual: refusal.Instances);

        // The control: a previous-frame buffer read breaks the loop.
        var set = Set(
            Bricks(reads: [BufferRead(previousFrame: true, producer: "lattice")]),
            Instance(
                name: "lattice",
                output: ShaderPipelineResourceKind.Buffer,
                reads: [BufferRead(producer: "bricks")]
            )
        );

        Assert.Equal(expected: [0, 1], actual: set.Order);
    }
    [Fact]
    public void ARootOrFootprintOverABufferIsRefusedAtTheFrame() {
        var set = Set(
            Instance(
                name: "main",
                reads: [BufferRead(producer: "bricks")]
            ),
            Bricks()
        );
        var schedule = new RenderGraphSchedule(set: set);

        Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
            frame: Frame(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "bricks", Width: 0.5)],
                index: 0,
                roots: [Full(instance: "main")]
            ),
            history: RenderGraphHistory.Empty(set: set),
            schedule: schedule,
            set: set
        ));
        Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
            frame: Frame(
                index: 0,
                roots: [Full(instance: "bricks")]
            ),
            history: RenderGraphHistory.Empty(set: set),
            schedule: schedule,
            set: set
        ));
        Assert.Equal(expected: -1L, actual: schedule.Frame);
    }
    [Fact]
    public void AnUndeclaredReadIsRefusedAtTheFrame() {
        var set = Set(
            Instance(name: "security"),
            Instance(name: "main")
        );

        var schedule = new RenderGraphSchedule(set: set);

        Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
            frame: Frame(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.2, Producer: "security", Width: 0.2)],
                index: 0,
                roots: [Full(instance: "main")]
            ),
            history: RenderGraphHistory.Empty(set: set),
            schedule: schedule,
            set: set
        ));
        Assert.Equal(expected: -1L, actual: schedule.Frame);
        Assert.Empty(collection: schedule.Renders);
        Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
            frame: Frame(
                index: 0,
                roots: [Full(instance: "main")]
            ),
            history: schedule.Next,
            schedule: schedule,
            set: set
        ));
    }
    [Fact]
    public void ASteadyFrameSchedulesWithoutAllocating() {
        var set = Set(
            Instance(name: "north"),
            Instance(name: "south"),
            Instance(
                name: "security",
                refresh: RenderGraphRefresh.Every(divisor: 3)
            ),
            Instance(
                name: "mirror",
                reads: [new RenderGraphRead(Producer: "mirror"), BufferRead(previousFrame: true, producer: "bricks")]
            ),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "north"), new RenderGraphRead(Producer: "south"), new RenderGraphRead(Producer: "security"), BufferRead(producer: "bricks")]
            ),
            Bricks()
        );
        RenderGraphRoot[] roots = [
            Full(instance: "main"),
            new RenderGraphRoot(Height: 0.25, Instance: "mirror", Width: 0.25),
        ];
        RenderGraphFootprint[] footprints = [
            new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "north", Width: 0.25),
            new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "south", Width: 0.25),
            new RenderGraphFootprint(Consumer: "main", Height: 0.2, Producer: "security", Width: 0.2),
            new RenderGraphFootprint(Consumer: "mirror", Height: 0.5, Producer: "mirror", Width: 0.5),
        ];
        // The budget fits one quarter-axis camera, so the north and south cameras alternate through the sort.
        const long Budget = (480 * 270);
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

        // Reusing the two schedules changes no result: the last frame matches a run that gave every frame a fresh one.
        var reference = Run(
            count: frame,
            frame: index => Frame(
                budget: Budget,
                footprints: footprints,
                index: index,
                roots: roots
            ),
            set: set
        )[^1];
        var last = schedules[((frame - 1) % 2)];

        Assert.Equal(expected: reference.Frame, actual: last.Frame);
        Assert.Equal(expected: reference.PassPixels, actual: last.PassPixels);
        Assert.Equal(expected: reference.Instances, actual: last.Instances);
        Assert.Equal(expected: reference.Renders, actual: last.Renders);
        Assert.Equal(expected: reference.Reads, actual: last.Reads);

        for (var index = 0; (index < set.Instances.Count); index++) {
            Assert.Equal(expected: reference.Next.LatestFrame(index: index), actual: last.Next.LatestFrame(index: index));
            Assert.Equal(expected: reference.Next.Allocated(index: index), actual: last.Next.Allocated(index: index));
        }

        void Step() {
            var schedule = schedules[(frame % 2)];

            RenderGraphScheduler.Schedule(
                frame: Frame(
                    budget: Budget,
                    footprints: footprints,
                    index: frame,
                    roots: roots
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
    public void AReusedScheduleMatchesAFreshOneWhileTheFrameChanges() {
        var set = Set(
            Instance(name: "north"),
            Instance(name: "south"),
            Instance(
                name: "security",
                refresh: RenderGraphRefresh.Every(divisor: 3)
            ),
            Instance(
                name: "mirror",
                reads: [new RenderGraphRead(Producer: "mirror"), BufferRead(previousFrame: true, producer: "bricks")]
            ),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "north"), new RenderGraphRead(Producer: "south"), new RenderGraphRead(Producer: "security"), BufferRead(producer: "bricks")]
            ),
            Bricks()
        );
        var north = new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "north", Width: 0.25);
        var south = new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "south", Width: 0.5);
        var security = new RenderGraphFootprint(Consumer: "main", Height: 0.2, Producer: "security", Width: 0.2);
        var mirror = new RenderGraphFootprint(Consumer: "mirror", Height: 0.5, Producer: "mirror", Width: 0.5);
        var corner = new RenderGraphRoot(Height: 0.25, Instance: "mirror", Width: 0.25);
        // Each frame changes what the one before it showed: roots and footprints come and go, a producer grows, and the
        // budget tightens, lifts and returns, so a reused schedule holds a larger frame's entries when a smaller follows.
        var frames = new Func<long, RenderGraphFrame>[] {
            index => Frame(budget: (480 * 270), footprints: [north, south, security, mirror], index: index, roots: [Full(instance: "main"), corner]),
            index => Frame(footprints: [north], index: index, roots: [Full(instance: "main")]),
            index => Frame(budget: (480 * 270), footprints: [mirror], index: index, roots: [corner]),
            index => Frame(budget: (960 * 540), footprints: [south, security], index: index, roots: [Full(instance: "main"), corner]),
            index => Frame(index: index, roots: [Full(instance: "north"), Full(instance: "south")]),
        };
        const long Count = 40;
        var reference = Run(
            count: Count,
            frame: index => frames[(index % frames.Length)](arg: index),
            set: set
        );
        RenderGraphSchedule[] schedules = [new(set: set), new(set: set)];
        var history = RenderGraphHistory.Empty(set: set);

        for (var index = 0L; (index < Count); index++) {
            var schedule = schedules[(index % 2)];
            var fresh = reference[((int)index)];

            RenderGraphScheduler.Schedule(
                frame: frames[(index % frames.Length)](arg: index),
                history: history,
                schedule: schedule,
                set: set
            );
            history = schedule.Next;

            Assert.Equal(expected: fresh.Frame, actual: schedule.Frame);
            Assert.Equal(expected: fresh.PassPixels, actual: schedule.PassPixels);
            Assert.Equal(expected: fresh.Instances, actual: schedule.Instances);
            Assert.Equal(expected: fresh.Renders, actual: schedule.Renders);
            Assert.Equal(expected: fresh.Reads, actual: schedule.Reads);

            for (var instance = 0; (instance < set.Instances.Count); instance++) {
                Assert.Equal(expected: fresh.Next.LatestFrame(index: instance), actual: schedule.Next.LatestFrame(index: instance));
                Assert.Equal(expected: fresh.Next.Allocated(index: instance), actual: schedule.Next.Allocated(index: instance));
            }
        }
    }
}
