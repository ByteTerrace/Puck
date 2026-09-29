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
            Assert.Equal(expected: (start + ((sample + 1) * SdfWorldPackage.NativeFragment.Passes.Count)), actual: view.Parts.Count);
            if (sample < 7) {
                Assert.False(condition: request.Completion.IsCompleted);
                Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
            }
        }
        Assert.True(condition: SpinWait.SpinUntil(condition: () => {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            Assert.Equal(expected: (start + (8 * SdfWorldPackage.NativeFragment.Passes.Count)), actual: view.Parts.Count);
            return request.Completion.IsCompleted;
        }, timeout: TimeSpan.FromSeconds(value: 30)));
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
            _ = frames.Next(); // Ready warmup is discarded before the capture-local sequence restarts.
            for (var sample = 0; (sample < 8); sample++) {
                _ = frames.Next();
                if (sample < 7) {
                    Assert.False(condition: request.Completion.IsCompleted);
                }
            }
            Assert.Null(@object: Outcome(request: request).Error);
        }
    }
    [InlineData(1, 0)]
    [InlineData(8, 0)]
    [InlineData(8, 3)]
    [Theory]
    public void ConvergenceRestartsPackageSamplesAfterTaintedWarmup(int samples, int precedingSamples) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var camera = new FakeCamera(gpu: gpu) { Filling = static () => false };
        var recorders = new Recorders();
        var view = new ViewPackage { SamplesReads = true };

        recorders.Registry.RegisterProducer(factory: _ => camera, package: Feed);
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(
            gpu, recorders,
            Set(
                new Puck.Hosting.RenderGraphInstance(ExternalPackage: Feed, Name: "camera", Passes: 1, Reads: [], Refresh: Puck.Hosting.RenderGraphRefresh.EveryFrame),
                PackageInstance() with { Reads = [new Puck.Hosting.RenderGraphRead(Producer: "camera")] }
            ),
            PackageView, new RenderGraphRuntimeGraph[2]
        );
        var frames = new Frames(
            footprints: [new Puck.Hosting.RenderGraphFootprint(Consumer: PackageView, Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new Puck.Hosting.RenderGraphRoot(Height: 1.0, Instance: PackageView, Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();
        var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: samples);

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        camera.Filling = static () => true;
        frames.Next(count: precedingSamples);
        Assert.False(condition: request.Completion.IsCompleted);
        camera.Filling = static () => false;
        frames.Next(count: 10);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.True(condition: (view.Samples[^1] >= 9));
        camera.Filling = static () => true;
        _ = frames.Next();
        Assert.False(condition: request.Completion.IsCompleted);
        var warmup = view.Samples.Count;

        for (var sample = 0; (sample < samples); sample++) {
            _ = frames.Next();
            Assert.Equal(expected: sample, actual: view.Samples[^1]);
            if (sample < (samples - 1)) { Assert.False(condition: request.Completion.IsCompleted); }
        }
        Assert.True(condition: SpinWait.SpinUntil(condition: () => {
            _ = frames.Next();
            Assert.Equal(expected: (warmup + samples), actual: view.Samples.Count);
            return request.Completion.IsCompleted;
        }, timeout: TimeSpan.FromSeconds(value: 30)));
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(expected: (warmup + samples), actual: view.Samples.Count);
        Assert.Equal(expected: 2, actual: view.Convergence.Count);
        Assert.All(collection: view.Convergence, action: reset => Assert.Same(actual: reset, expected: request));
    }
}
