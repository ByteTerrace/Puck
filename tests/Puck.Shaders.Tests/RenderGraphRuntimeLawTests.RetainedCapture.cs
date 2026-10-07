using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(0)]
    [InlineData(2)]
    [Theory]
    public void AFinitePackageHoldKeepsItsExactOutputWithoutSubmittingWhileCaptureInputsAdvance(int converge) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var camera = new FakeCamera(gpu: gpu) { Filling = static () => true };
        var reader = new ReadingWorld();
        var recorders = new Recorders();
        var view = new ViewPackage { SamplesReads = true };

        recorders.Registry.RegisterProducer(factory: _ => camera, package: Feed);
        recorders.Registry.RegisterProducer(factory: _ => reader, package: World);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(
            new RenderGraphInstance(ExternalPackage: Feed, Name: "camera", Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
            PackageInstance() with { Reads = [new RenderGraphRead(Producer: "camera")] },
            new RenderGraphInstance(ExternalPackage: World, Name: "reader", Passes: WorldPasses,
                Reads: [new RenderGraphRead(Producer: PackageView)], Refresh: RenderGraphRefresh.EveryFrame)),
            "reader", new RenderGraphRuntimeGraph[3]);
        var frames = new Frames(
            footprints: [new(Consumer: PackageView, Height: 1, Producer: "camera", Width: 1),
                new(Consumer: "reader", Height: 1, Producer: PackageView, Width: 1)],
            roots: [new(Height: 1, Instance: "reader", Width: 1)], runtime: runtime);

        frames.Settle();
        var node = runtime.NodeOf(instance: PackageView)!;
        var publication = view.Publication;
        var image = node.PublishedSurface;
        var recorded = node.FrameCounter;
        var submitted = gpu.SubmissionsMade;

        Assert.True(condition: publication.IsKnown);
        view.HoldOutput = true;
        view.CaptureState = FrameRender.Waiting(reason: "a sibling has not consumed the held image");
        var request = new FrameCaptureRequest(CaptureRequest().Path, converge: converge);

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        var capture = Assert.Single(collection: view.Convergence);
        // More than a frame ring: a pass-level skip would still submit, rotate its images and rearm its fences.
        for (var frame = 0; (frame < 12); frame++) {
            var filled = ((frame & 1) == 0);

            camera.Filling = () => filled;
            frames.Next();
            Assert.Equal(submitted, gpu.SubmissionsMade);
            Assert.Equal(recorded, node.FrameCounter);
            Assert.Equal(publication, view.Publication);
            Assert.Equal(image, node.PublishedSurface);
            Assert.Equal(publication, Assert.Single(collection: reader.Publications));
            Assert.True(condition: runtime.Render.IsRendered);
            Assert.Equal(0, capture.Samples);
            Assert.False(condition: request.Completion.IsCompleted);
        }
        camera.Filling = static () => true;
        view.HoldOutput = false;
        view.CaptureState = FrameRender.Rendered;
        TestLiveness.Until(step: () => { frames.Next(); return request.Completion.IsCompleted; });
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(converge, capture.Samples);
        Assert.True(condition: (view.Publication.Sequence > publication.Sequence));
        Assert.True(condition: (gpu.SubmissionsMade > submitted));
    }
    [InlineData(0)]
    [InlineData(2)]
    [Theory]
    public void CaptureWaitsForTheDependencyPackagesColdSourceBeforeCountingOrForwarding(int converge) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage { CaptureState = FrameRender.Waiting(reason: "cold source has not completed") };

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance(), Instance(name: "main", reads: new RenderGraphRead(Producer: PackageView))),
            "main", null!, Graph(ScreensGraph(false, "view"), ("view", PackageView)));
        var frames = new Frames(
            footprints: [new(Consumer: "main", Height: 1, Producer: PackageView, Width: 1)],
            roots: [new(Height: 1, Instance: "main", Width: 1)], runtime: runtime);

        frames.Settle();
        var request = new FrameCaptureRequest(CaptureRequest().Path, converge: converge);

        runtime.RequestCapture(request: request);
        var capture = Assert.Single(collection: view.Convergence);

        Assert.Same(request, capture.Request);
        frames.Next(count: 3);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Equal(0, capture.Samples);
        Assert.Equal("cold source has not completed", runtime.UnservedCaptureReason);
        view.CaptureState = FrameRender.Rendered;
        TestLiveness.Until(step: () => { frames.Next(); return request.Completion.IsCompleted; });
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(converge, capture.Samples);
    }
    [Fact]
    public void CleanCurrentInputsCannotRemoveTheRetainedPackagesExternalTaint() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage { RetainedTainted = true };

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance(), Instance(name: "main", reads: new RenderGraphRead(Producer: PackageView))),
            "main", null!, Graph(ScreensGraph(false, "view"), ("view", PackageView)));
        var frames = new Frames(
            footprints: [new(Consumer: "main", Height: 1, Producer: PackageView, Width: 1)],
            roots: [new(Height: 1, Instance: "main", Width: 1)], runtime: runtime);

        frames.Settle();
        var request = CaptureRequest();

        runtime.RequestCapture(request: request);
        frames.Next(count: 3);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Contains("external content", Assert.IsType<string>(@object: runtime.UnservedCaptureReason), StringComparison.Ordinal);
        view.RetainedTainted = false;
        TestLiveness.Until(step: () => { frames.Next(); return request.Completion.IsCompleted; });
        Assert.Null(@object: Outcome(request: request).Error);
    }
    [Fact]
    public void ARefusedRetainedCaptureSourceFailsByNameInsteadOfWaiting() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;

        TestLiveness.Until(step: () => { ProducePackageFrame(frameIndex: index++, runtime: runtime); return (view.Parts.Count > 0); });
        view.CaptureState = FrameRender.Refused(reason: "retained source is frozen");
        var request = CaptureRequest();

        runtime.RequestCapture(request: request);
        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Equal("retained source is frozen", Assert.IsType<InvalidOperationException>(@object: Outcome(request: request).Error).Message);
    }
}
