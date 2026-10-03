namespace Puck.Hosting.Tests;

public sealed partial class RenderGraphSchedulerLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AHistoryReadCreatesNoProducerDemandEvenBeforeItsFirstWrite(bool buffer) {
        var kind = (buffer ? ShaderPipelineResourceKind.Buffer : ShaderPipelineResourceKind.Image);
        var set = Set(
            Instance(name: "reader", reads: [new RenderGraphRead(Kind: kind, PreviousFrame: true, Producer: "writer")]),
            Instance(name: "writer", output: kind));
        var schedules = Run(set: set, count: 4, frame: index => Frame(index: index, roots: [Full(instance: "reader")],
            footprints: (buffer ? [] : [new RenderGraphFootprint(Consumer: "reader", Height: 1, Producer: "writer", Width: 1)])));

        Assert.Equal(0, RenderCount(name: "writer", schedules: schedules, set: set));
        Assert.All(collection: schedules, action: schedule => {
            Assert.Equal(-1, Assert.Single(collection: schedule.Reads).Frame);
            Assert.Equal((0d, 0d), schedule.Next.Allocated(index: set.IndexOf(name: "writer")));
            Assert.Equal(RenderGraphInstanceStatus.Waiting, Row(name: "writer", schedule: schedule, set: set).Status);
        });
    }
    [Fact]
    public void AHistoryReadKeepsAnIndependentlyWrittenFrameWithoutRefreshingOrResizingIt() {
        var set = Set(Instance(name: "reader", reads: [new RenderGraphRead(Producer: "writer", PreviousFrame: true)]), Instance(name: "writer"));
        var schedules = Run(set: set, count: 5, frame: index => Frame(index: index,
            roots: ((index == 0) ? [Full(instance: "reader"), new RenderGraphRoot(Height: 0.25, Instance: "writer", Width: 0.25)] : [Full(instance: "reader")]),
            footprints: [new RenderGraphFootprint(Consumer: "reader", Height: 1, Producer: "writer", Width: 1)]));

        Assert.Equal(1, RenderCount(name: "writer", schedules: schedules, set: set));
        Assert.All(collection: schedules.Skip(count: 1), action: schedule => {
            Assert.Equal(0, Assert.Single(collection: schedule.Reads).Frame);
            Assert.Equal((0.25, 0.25), schedule.Next.Allocated(index: set.IndexOf(name: "writer")));
        });
    }
    [Fact]
    public void AWithdrawnWriterRestoresItsPriorHistoryAndAllocatedExtent() {
        var set = Set(Instance(name: "writer"));
        var schedules = Run(set: set, count: 2, frame: index => Frame(index: index,
            roots: [new RenderGraphRoot(Height: ((index == 0) ? 0.25 : 1), Instance: "writer", Width: ((index == 0) ? 0.25 : 1))]));

        schedules[1].Next.Withdraw(index: 0, previous: schedules[0].Next);
        Assert.Equal(0, schedules[1].Next.LatestFrame(index: 0));
        Assert.Equal((0.25, 0.25), schedules[1].Next.Allocated(index: 0));
    }
    // A root's refresh limits how often it asks for its instance and never starves it: the instance renders on its first
    // frame, then once per the root's period, a resize lands at the next period at the new extent, and an empty history
    // (a reset or a device loss) renders it at once.
    [Fact]
    public void ARootRefreshedEveryThirdFrameRendersAtOnceThenOncePerPeriodAcrossAResizeAndAReset() {
        var set = Set(Instance(name: "view"));

        RenderGraphRoot Root(double extent) => new(Height: extent, Instance: "view", Width: extent) { Refresh = RenderGraphRefresh.Every(divisor: 3) };
        var schedules = Run(set: set, count: 12, frame: index => Frame(index: index, roots: [Root(extent: ((index < 7) ? 0.5 : 1))]));

        Assert.Equal(
            expected: [0L, 3L, 6L, 9L],
            actual: schedules.Where(predicate: static schedule => schedule.Renders.Contains(value: 0)).Select(selector: static schedule => schedule.Frame)
        );
        Assert.Equal(expected: (0.5, 0.5), actual: schedules[8].Next.Allocated(index: 0));
        Assert.Equal(expected: (1d, 1d), actual: schedules[9].Next.Allocated(index: 0));

        var reset = new RenderGraphSchedule(set: set);

        RenderGraphScheduler.Schedule(frame: Frame(index: 13, roots: [Root(extent: 1)]), history: RenderGraphHistory.Empty(set: set), schedule: reset, set: set);
        Assert.Contains(expected: 0, collection: reset.Renders);
    }
}
