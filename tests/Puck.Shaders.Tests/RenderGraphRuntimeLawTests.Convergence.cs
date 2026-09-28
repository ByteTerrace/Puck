using Puck.Abstractions.Presentation;

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

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);
                return (view.Parts.Count > 0);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        view.Unchanged = true;
        var start = view.Parts.Count;
        var request = new FrameCaptureRequest(path: Path.Combine(path1: Path.GetTempPath(), path2: $"{Guid.NewGuid():N}.png"), converge: 8);

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        Assert.Same(expected: request, actual: Assert.Single(collection: view.Convergence));
        for (var sample = 0; (sample < 8); sample++) {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            Assert.Equal(expected: (start + ((sample + 1) * SdfWorldPackage.Fragment.Passes.Count)), actual: view.Parts.Count);
            if (sample < 7) {
                Assert.False(condition: request.Completion.IsCompleted);
                Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
            }
        }
        Assert.True(condition: SpinWait.SpinUntil(condition: () => {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            Assert.Equal(expected: (start + (8 * SdfWorldPackage.Fragment.Passes.Count)), actual: view.Parts.Count);
            return request.Completion.IsCompleted;
        }, timeout: TimeSpan.FromSeconds(value: 30)));
        ProducePackageFrame(frameIndex: index++, runtime: runtime);
        Assert.Equal(expected: (start + (8 * SdfWorldPackage.Fragment.Passes.Count)), actual: view.Parts.Count);
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

}
