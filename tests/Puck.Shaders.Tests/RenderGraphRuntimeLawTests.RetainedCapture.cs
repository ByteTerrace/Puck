using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void CaptureWaitsForTheDependencyPackagesColdSourceBeforeCountingOrForwarding(int converge) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage { CaptureState = FrameRender.Waiting("cold source has not completed") };
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance(), Instance(name: "main", reads: new RenderGraphRead(Producer: PackageView))),
            "main", null!, Graph(ScreensGraph(false, "view"), ("view", PackageView)));
        var frames = new Frames(
            footprints: [new(Consumer: "main", Producer: PackageView, Width: 1, Height: 1)],
            roots: [new(Instance: "main", Width: 1, Height: 1)], runtime: runtime);
        frames.Settle();
        var request = new FrameCaptureRequest(CaptureRequest().Path, converge: converge);
        runtime.RequestCapture(request);
        var capture = Assert.Single(view.Convergence);
        Assert.Same(request, capture.Request);
        frames.Next(count: 3);
        Assert.False(request.Completion.IsCompleted);
        Assert.Equal(0, capture.Samples);
        Assert.Equal("cold source has not completed", runtime.UnservedCaptureReason);
        view.CaptureState = FrameRender.Rendered;
        TestLiveness.Until(step: () => { frames.Next(); return request.Completion.IsCompleted; });
        Assert.Null(Outcome(request).Error);
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
            footprints: [new(Consumer: "main", Producer: PackageView, Width: 1, Height: 1)],
            roots: [new(Instance: "main", Width: 1, Height: 1)], runtime: runtime);
        frames.Settle();
        var request = CaptureRequest();
        runtime.RequestCapture(request);
        frames.Next(count: 3);
        Assert.False(request.Completion.IsCompleted);
        Assert.Contains("external content", Assert.IsType<string>(runtime.UnservedCaptureReason), StringComparison.Ordinal);
        view.RetainedTainted = false;
        TestLiveness.Until(step: () => { frames.Next(); return request.Completion.IsCompleted; });
        Assert.Null(Outcome(request).Error);
    }

    [Fact]
    public void ARefusedRetainedCaptureSourceFailsByNameInsteadOfWaiting() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var view = new ViewPackage();
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;
        TestLiveness.Until(step: () => { ProducePackageFrame(frameIndex: index++, runtime: runtime); return view.Parts.Count > 0; });
        view.CaptureState = FrameRender.Refused("retained source is frozen");
        var request = CaptureRequest();
        runtime.RequestCapture(request);
        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.True(request.Completion.IsCompleted);
        Assert.Equal("retained source is frozen", Assert.IsType<InvalidOperationException>(Outcome(request).Error).Message);
    }
}
