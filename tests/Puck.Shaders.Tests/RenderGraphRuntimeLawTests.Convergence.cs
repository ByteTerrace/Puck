using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Theory]
    [InlineData("absent")]
    [InlineData("zero")]
    [InlineData("image")]
    [InlineData("previous")]
    [InlineData("buffer")]
    [InlineData("removed")]
    public void CaptureCompletionFollowsItsVisibleImagesAndBufferDependencies(string edge) {
        const string Inactive = "inactive";
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var buffer = edge == "buffer";
        var package = new CaptureDemandPackage(buffer);
        recorders.Registry.Register(buffer ? RenderGraphPackageCatalog.Indirect : RenderGraphPackageCatalog.SdfWorld, package);
        using var runtime = buffer
            ? Runtime(gpu, recorders, NamedBufferInstances(), "main", null!, NamedBufferReader(null))
            : Runtime(gpu, recorders, Set(PackageInstance(), PackageInstance() with { Name = Inactive },
                Instance("main", reads: [new(Producer: PackageView), new(Producer: Inactive, PreviousFrame: edge == "previous")])),
                "main", null!, null!, Graph(ScreensGraph(false, "view"), ("view", PackageView)));
        var footprints = new List<RenderGraphFootprint>();
        if (!buffer) {
            footprints.Add(new("main", PackageView, 1, 1));
            if (edge != "absent") { footprints.Add(new("main", Inactive, edge == "zero" ? 0 : 1, 1)); }
        }
        var frames = new Frames(runtime, [new("main", 1, 1)], footprints);
        if (edge == "previous") {
            // The previous image is a real completed publication, then stands without current producer demand.
            new Frames(runtime, [new("main", 1, 1), new(Inactive, 1, 1)], footprints).Settle();
        }
        frames.Settle();
        if (buffer) { runtime.Node(0).Paused = true; }
        var request = new FrameCaptureRequest(CaptureRequest().Path);
        runtime.RequestCapture(request);
        var demanded = buffer || edge is "image" or "previous" or "removed";
        if (demanded) {
            frames.Next(3);
            Assert.False(request.Completion.IsCompleted);
            Assert.Contains("pending demanded source", runtime.UnservedCaptureReason);
            Assert.Single(package.Started, name => name == (buffer ? "pool" : Inactive));
            // A standing buffer or previous image still owns its fence. Repeated demand never restarts that operation.
            if (edge == "removed") { footprints.RemoveAll(item => item.Producer == Inactive); }
            else { package.Ready = true; }
        } else {
            Assert.DoesNotContain(Inactive, package.Started);
            Assert.Equal(0UL, runtime.NodeOf(Inactive)!.FrameCounter);
        }
        TestLiveness.Within(frames: 64, step: () => { frames.Next(); return request.Completion.IsCompleted; },
            building: () => runtime.Instances.Instances.Any(instance => runtime.NodeOf(instance.Name)?.IsBuildingCandidate == true),
            reason: () => runtime.UnservedCaptureReason);
        Assert.Null(Outcome(request).Error);
        if (demanded) { Assert.Single(package.Started, name => name == (buffer ? "pool" : Inactive)); }
    }

    private sealed class CaptureDemandPackage(bool buffer) : IRenderGraphPackageFactory {
        private readonly IRenderGraphPackageFactory m_inner = buffer ? new NamedBufferPackage() : new ViewPackage();
        public List<string> Started { get; } = [];
        public bool Ready { get; set; }
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            m_inner.BuildAsync(context, cancellationToken);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            m_inner.Create(context, built, groups);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => m_inner.CounterOf(instance);
        public RenderGraphPackageFragment? FragmentOf(string instance) => m_inner.FragmentOf(instance);
        public FrameRender CaptureReadinessOf(string instance) => Ready || (!buffer && instance == PackageView)
            ? FrameRender.Rendered : FrameRender.Waiting("pending demanded source");
        public void BeginConvergence(string instance, RenderGraphConvergence convergence) => Started.Add(instance);
    }

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
