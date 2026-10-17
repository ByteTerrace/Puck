using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
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
}
