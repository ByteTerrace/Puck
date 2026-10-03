using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ConvergingCaptureRendersExactlyTheRequestedSamplesBeforeServing() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;

        TestLiveness.Until(
            step: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);
                return (view.Parts.Count > 0);
            }
        );
        view.Unchanged = true;
        var start = view.Parts.Count;
        var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: 8);

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        Assert.Same(expected: request, actual: Assert.Single(collection: view.Convergence).Request);
        for (var sample = 0; (sample < 8); sample++) {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            Assert.Equal(expected: (start + ((sample + 1) * SdfWorldPackage.NativeFragment.Passes.Count)), actual: view.Parts.Count);
            if (sample < 7) {
                Assert.False(condition: request.Completion.IsCompleted);
                Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
            }
        }
        TestLiveness.Until(
            step: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);
                Assert.Equal(expected: (start + (8 * SdfWorldPackage.NativeFragment.Passes.Count)), actual: view.Parts.Count);
                return request.Completion.IsCompleted;
            }
        );
        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.Equal(expected: (start + (8 * SdfWorldPackage.NativeFragment.Passes.Count)), actual: view.Parts.Count);
    }
    [Fact]
    public void ConvergenceCountsOnlyFramesWhoseDependenciesAreReadyForCapture() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, camera) = TaintScene(gpu: gpu);
        using (runtime) {
            camera.Filling = static () => false;
            PastAViewRender(frames: frames, runtime: runtime);
            var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: 8);

            runtime.RequestCapture(request: request);
            for (var frame = 0; (frame < 10); frame++) {
                _ = frames.Next();
                Assert.False(condition: request.Completion.IsCompleted);
            }
            camera.Filling = static () => true;
            for (var sample = 0; (sample < 8); sample++) {
                _ = frames.Next();
                if (sample < 7) {
                    Assert.False(condition: request.Completion.IsCompleted);
                }
            }
            Assert.Null(@object: Outcome(request: request).Error);
        }
    }
    // A package samples on the capture's behalf at the index the runtime counted: every render while its read is tainted
    // takes sample 0 again, the ready renders take 0 to N-1, and the Nth ready render is the one served.
    [Fact]
    public void ConvergingPackagesTakeTheSampleTheRuntimeCountedAndTheServedRenderIsTheLast() {
        const int Converge = 4;
        const int Tainted = 6;
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var camera = new FakeCamera(gpu: gpu);
        var recorders = new Recorders();
        var view = new ViewPackage { SamplesReads = true };

        recorders.Registry.RegisterProducer(factory: _ => camera, package: Feed);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(
            new RenderGraphInstance(ExternalPackage: Feed, Name: "camera", Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
            (PackageInstance() with { Reads = [new RenderGraphRead(Producer: "camera")] })
        ), PackageView, new RenderGraphRuntimeGraph[2]);
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: PackageView, Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: PackageView, Width: 1.0)],
            runtime: runtime
        );

        TestLiveness.Until(
            step: () => {
                _ = frames.Next();
                return ((view.Parts.Count > 0) && runtime.IsSettled);
            }
        );
        var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: Converge);

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        frames.Next(count: Tainted);
        Assert.Equal(expected: Enumerable.Repeat(count: Tainted, element: 0), actual: view.Served);
        camera.Filling = static () => true;
        TestLiveness.Until(
            step: () => {
                _ = frames.Next();
                return request.Completion.IsCompleted;
            }
        );
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(expected: Enumerable.Repeat(count: Tainted, element: 0).Concat(second: Enumerable.Range(count: Converge, start: 0)), actual: view.Served);
    }
}
