using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(true, true, false, FrameCompletion.NotYetRenderable)]
    [InlineData(false, true, false, FrameCompletion.NotYetRenderable)]
    [InlineData(false, false, false, FrameCompletion.Rendered)]
    [InlineData(true, true, true, FrameCompletion.Rendered)]
    [InlineData(false, true, true, FrameCompletion.Rendered)]
    [Theory]
    public void CompletionFollowsScheduledExternalAndScreenReads(bool external, bool samples, bool previous, FrameCompletion expected) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeProducer(gpu: gpu);
        var view = new FakeProducer(gpu: gpu);

        recorders.Registry.RegisterProducer(factory: context => ((context.Instance == ReadSource) ? source : view), package: World);
        recorders.Registry.Register(factory: new ReadingPackage(samples: samples), package: Camera);

        var read = new RenderGraphRead(Producer: ReadSource, PreviousFrame: previous);
        var consumer = (external
            ? new RenderGraphInstance(ExternalPackage: World, Name: "view", Passes: 1, Reads: [read], Refresh: RenderGraphRefresh.EveryFrame)
            : Instance(name: "view", reads: read));

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(External(name: ReadSource), consumer),
            "view",
            null!,
            (external ? null! : Graph(pipeline: CameraGraph()))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: ReadSource, Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "view", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();
        source.Holding = true;

        var produced = source.Produced;

        for (var frame = 0; (frame < 3); frame++) {
            // The consumer still submits an image, but a same-frame read shows the held source's older state.
            Assert.False(condition: frames.Next().IsEmpty);
            Assert.Equal(expected: produced, actual: source.Produced);
            Assert.Equal(expected: expected, actual: runtime.Render.Completion);
        }

        source.Holding = false;
        _ = frames.Next();
        Assert.Equal(expected: FrameCompletion.Rendered, actual: runtime.Render.Completion);
    }
    [Fact]
    public void AnUnshownInputDoesNotKeepTheRootWaiting() {
        var gpu = new FakePipelineGpu();

        var (runtime, _, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var footprints = new List<RenderGraphFootprint> {
                new(Consumer: "main", Height: 1.0, Producer: "world", Width: 1.0),
            };
            var frames = new Frames(
                footprints: footprints,
                roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
                runtime: runtime
            );

            frames.Settle();
            producers.Only.Holding = true;
            _ = frames.Next();
            Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: runtime.Render.Completion);

            footprints.Clear();
            frames.Next(count: 3);
            Assert.Equal(expected: RenderGraphInstanceStatus.Unread, actual: runtime.Latest!.Instances[0].Status);
            Assert.Equal(expected: FrameCompletion.Rendered, actual: runtime.Render.Completion);
            Assert.Null(@object: runtime.Render.Reason);
        }
    }
    [Fact]
    public void ARefusedBufferProducerRefusesItsBlockedConsumerEvenWhileAnotherInputBuilds() {
        var gpu = new FakePipelineGpu();
        var package = new RefusingPackage(package: Pool) { Refusal = "the buffer build is refused", RefusedInstance = "pool" };

        package.Registry.Register(factory: package, package: Camera);

        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: gpu,
            graphs: [Graph(pipeline: CameraGraph()), Graph(pipeline: PoolGraph()), Graph(ScreensGraph(true, "screen"), ("screen", "camera"), ("pool", "pool"))],
            hostsOnDirectX: false,
            packages: package.Registry,
            pipelines: new GpuPassPipelineCache(),
            refusal: out var refusal,
            root: "main",
            runtime: out var runtime,
            set: Set(
                Instance(name: "camera"),
                Instance(name: "pool", output: ShaderPipelineResourceKind.Buffer),
                Instance(name: "main", reads: [new RenderGraphRead(Producer: "camera"), new RenderGraphRead(Producer: "pool", Kind: ShaderPipelineResourceKind.Buffer)])
            )
        ), userMessage: refusal?.Message);

        using (runtime) {
            var frames = new Frames(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
                roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
                runtime: runtime
            );

            frames.Next(count: 3);
            Assert.Equal(expected: FrameCompletion.Refused, actual: runtime.Render.Completion);
            Assert.Contains(expectedSubstring: package.Refusal!, actualString: runtime.Render.Reason);
        }
    }
    [Fact]
    public void AMissingGraphRefusesTheFrameUntilAGraphIsInstalled() {
        var gpu = new FakePipelineGpu();

        using var runtime = Runtime(gpu, new Recorders(Camera), Set(Instance(name: "main")), "main", new RenderGraphRuntimeGraph[1]);
        var frames = new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        frames.Next(count: 3);
        Assert.False(condition: runtime.Node(instance: 0).IsBuildingCandidate);
        Assert.Equal(expected: FrameCompletion.Refused, actual: runtime.Render.Completion);
        Assert.Contains(expectedSubstring: "no graph is installed", actualString: runtime.Render.Reason);
        Assert.True(condition: runtime.TryInstall(instance: "main", graph: Graph(pipeline: CameraGraph()), refusal: out var refusal), userMessage: refusal?.Message);
        frames.Settle();
        Assert.Equal(expected: FrameCompletion.Rendered, actual: runtime.Render.Completion);
    }
    [Fact]
    public void ARefusedSourceConversionIsNotAWaitForPixels() {
        var gpu = new FakePipelineGpu { FailAtCreation = 1 };
        var recorders = new Recorders();

        SourceConversionPackage.RegisterAll(packages: recorders.Registry);
        recorders.Registry.RegisterSource(factory: _ => new FakeUpload(format: Puck.Abstractions.Sources.ImagePixelFormat.B8G8R8A8Unorm), package: Upload);

        using var runtime = Runtime(gpu, recorders, Set(RenderGraphInstance.Source(name: "pattern", producer: "test")), "pattern", new RenderGraphRuntimeGraph[1]);
        var frames = new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "pattern", Width: 1.0)],
            runtime: runtime
        );

        TestLiveness.Until(step: () => {
            _ = frames.Next();

            return (runtime.Node(instance: 0).LastSwapError is not null);
        });
        frames.Next(count: 3);
        Assert.Equal(expected: FrameCompletion.Refused, actual: runtime.Render.Completion);
        Assert.Contains(expectedSubstring: runtime.Node(instance: 0).LastSwapError!.Message, actualString: runtime.Render.Reason);
    }
}
