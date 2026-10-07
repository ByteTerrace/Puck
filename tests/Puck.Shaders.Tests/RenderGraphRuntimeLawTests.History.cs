using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    private static RenderGraphRoot Full(string instance) => new(Height: 1, Instance: instance, Width: 1);

    [Fact]
    public void APreviousFrameReaderKeepsTheLastSuccessfulOutputWithoutDemandingItsWriter() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera, Over);
        using var runtime = Runtime(gpu, recorders,
            Set(Instance(name: "writer"), Instance(name: "main", reads: new RenderGraphRead(Producer: "writer", PreviousFrame: true))),
            "main", Graph(CameraGraph()), Graph(OverGraph(reader: false), ("world", "writer")));
        RenderGraphFootprint[] footprints = [new(Consumer: "main", Height: 1, Producer: "writer", Width: 1)];
        var seed = new Frames(runtime, [Full(instance: "writer"), Full(instance: "main")], footprints);

        seed.Settle();
        var records = recorders.Of(instance: "writer").Records;
        var image = recorders.Of(instance: "writer").OutputImage;
        var reader = new Frames(runtime, [Full(instance: "main")], footprints);

        reader.Next(count: 9);
        Assert.Equal(records, recorders.Of(instance: "writer").Records);
        Assert.Equal(image, recorders.Of(instance: "main").InputImage);
        Assert.Single(collection: runtime.Latest!.Renders);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AFailedWriterAndUnattemptedWritersLeaveNoSchedulingHistory(bool deviceLoss) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        using var runtime = Runtime(gpu, recorders, Set(Instance("first"), Instance("second")), "second",
            Graph(CameraGraph()), Graph(CameraGraph()));
        var frames = new Frames(runtime, [Full(instance: "first"), Full(instance: "second")], []);

        frames.Settle();
        var previous = runtime.Latest!.Next;
        var first = previous.LatestFrame(index: 0);
        var second = previous.LatestFrame(index: 1);

        recorders.Of(instance: "first").RefuseRecording = true;
        Assert.Throws<InvalidOperationException>(testCode: () => frames.Next());
        Assert.Equal(first, runtime.Latest!.Next.LatestFrame(index: 0));
        Assert.Equal(second, runtime.Latest.Next.LatestFrame(index: 1));
        if (deviceLoss) {
            runtime.OnDeviceLost();
            Assert.Null(@object: runtime.Latest);
        }
        recorders.Of(instance: "first").RefuseRecording = false;
        frames.Settle();
        Assert.True(condition: (runtime.Latest!.Next.LatestFrame(index: 0) > first));
        Assert.True(condition: (runtime.Latest.Next.LatestFrame(index: 1) > second));
    }
    // A paused consumer presents its last image on purpose, so each frame it is scheduled for is spent as its refresh
    // counts it: it is due, and passes demand to the producer it reads within the frame, once per refresh period, and
    // the producer renders no more often than that.
    [Fact]
    public void APausedConsumerDemandsItsProducerOncePerRefreshPeriod() {
        const int Divisor = 3;
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera, Over);
        using var runtime = Runtime(gpu, recorders,
            Set(Instance(name: "writer"), (Instance(name: "main", reads: new RenderGraphRead(Producer: "writer")) with { Refresh = RenderGraphRefresh.Every(divisor: Divisor) })),
            "main", Graph(CameraGraph()), Graph(OverGraph(reader: false), ("world", "writer")));
        var frames = new Frames(runtime, [Full(instance: "main")], [new RenderGraphFootprint(Consumer: "main", Height: 1, Producer: "writer", Width: 1)]);

        frames.Settle();
        runtime.Node(instance: runtime.Instances.IndexOf(name: "main"))!.Paused = true;
        var records = recorders.Of(instance: "writer").Records;

        frames.Next(count: (Divisor * 4));
        Assert.Equal(expected: (records + 4), actual: recorders.Of(instance: "writer").Records);
    }
}
