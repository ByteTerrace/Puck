using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(false, FrameCompletion.NotYetRenderable)]
    [InlineData(false, FrameCompletion.Refused)]
    [InlineData(false, FrameCompletion.Rendered)]
    [InlineData(true, FrameCompletion.NotYetRenderable)]
    [InlineData(true, FrameCompletion.Refused)]
    [InlineData(true, FrameCompletion.Rendered)]
    [Theory]
    public void AnUnscheduledSourceAnswersWithoutProducingOrAllocating(bool zeroExtent, FrameCompletion completion) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var source = new FakeSource(gpu: gpu, cadence: ImageSourceCadence.Rate(rateHz: 30U)) {
            Availability = completion switch {
                FrameCompletion.Refused => FrameRender.Refused(reason: "the source cannot open"),
                FrameCompletion.Rendered => FrameRender.Rendered,
                _ => FrameRender.Waiting(reason: "the source awaits its first frame"),
            },
        };

        if (zeroExtent) {
            source.Descriptor = source.Descriptor! with { Width = 0U, Height = 0U };
        }

        recorders.Registry.RegisterProducer(factory: _ => source, package: RenderGraphInstance.SourcePackage(producer: ReadProducer));
        using var runtime = Runtime(gpu, recorders, Set(RenderGraphInstance.Source(name: ReadSource, producer: ReadProducer)), ReadSource, ((RenderGraphRuntimeGraph)null!));
        var frame = new RenderGraphFrame(
            DisplayHeight: Display,
            DisplayHertz: (zeroExtent ? 60 : 0),
            DisplayWidth: Display,
            Footprints: [],
            Index: 0L,
            Roots: [new RenderGraphRoot(Height: 1.0, Instance: ReadSource, Width: 1.0)],
            Tick: 1L
        );

        for (var index = 0; (index < 32); index++) {
            frame = frame with { Index = index };
            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 32; (index < 160); index++) {
            frame = frame with { Index = index };
            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(actual: allocated, expected: 0L);
        Assert.Equal(expected: 0, actual: source.Produced);
        Assert.Equal(expected: 160, actual: source.Answered);
        Assert.Equal(expected: completion, actual: runtime.Render.Completion);

        // A completion change with the same diagnostic still propagates, and a new diagnostic replaces the cached one.
        source.Availability = FrameRender.Refused(reason: "the source awaits its first frame");
        frame = frame with { Index = 160L };
        _ = runtime.ProduceFrame(context: default, frame: in frame);
        Assert.Equal(expected: FrameCompletion.Refused, actual: runtime.Render.Completion);
        source.Availability = FrameRender.Waiting(reason: "the source restarted");
        frame = frame with { Index = 161L };
        _ = runtime.ProduceFrame(context: default, frame: in frame);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: runtime.Render.Completion);
        Assert.Contains(expectedSubstring: "the source restarted", actualString: runtime.Render.Reason);
    }
}
