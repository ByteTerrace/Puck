using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AForwardedExternalCaptureKeepsItsDependencyEpochWhenDemandChanges() {
        const string Inactive = "inactive";
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var producers = new Producers(gpu: gpu);
        var package = new CaptureDemandPackage(buffer: false) { Ready = true };

        producers.Register(registry: recorders.Registry);
        recorders.Registry.Register(factory: package, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance(), PackageInstance() with { Name = Inactive },
            External("world") with { Reads = [new(Producer: PackageView), new(Producer: Inactive)] }),
            "world", null!, null!, null!);
        var footprints = new List<RenderGraphFootprint> { new(Consumer: "world", Height: 1, Producer: PackageView, Width: 1) };
        // Both sources own real outputs before the capture, but only one contributes to this capture's footprint.
        new Frames(runtime, [new(Height: 1, Instance: "world", Width: 1), new(Height: 1, Instance: PackageView, Width: 1), new(Height: 1, Instance: Inactive, Width: 1)], footprints).Settle();
        var frames = new Frames(runtime, [new(Height: 1, Instance: "world", Width: 1)], footprints);
        var producer = producers.Only;
        var request = CaptureRequest();

        producer.Holding = true;
        runtime.RequestCapture(request: request);
        frames.Next();
        Assert.Equal(request.Path, producer.PendingCapturePath);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Empty(collection: producer.Captured);
        Assert.Equal(PackageView, Assert.Single(collection: package.Started));

        footprints.Add(item: new(Consumer: "world", Height: 1, Producer: Inactive, Width: 1));
        frames.Next(count: 3);
        Assert.Equal(request.Path, producer.PendingCapturePath);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Equal(PackageView, Assert.Single(collection: package.Started));
        producer.Holding = false;
        frames.Next();
        Assert.Equal(request.Path, Assert.Single(collection: producer.Captured));
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Null(@object: producer.PendingCapturePath);
    }
    [InlineData("absent")]
    [InlineData("zero")]
    [InlineData("image")]
    [InlineData("previous")]
    [InlineData("buffer")]
    [InlineData("removed")]
    [InlineData("prepared")]
    [Theory]
    public void CaptureCompletionFollowsItsVisibleImagesAndBufferDependencies(string edge) {
        const string Inactive = "inactive";
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders();
        var buffer = (edge == "buffer");
        var package = new CaptureDemandPackage(buffer: buffer);

        recorders.Registry.Register(factory: package, package: (buffer ? RenderGraphPackageCatalog.Indirect : RenderGraphPackageCatalog.SdfWorld));
        using var runtime = (buffer
            ? Runtime(gpu, recorders, NamedBufferInstances(), "main", null!, NamedBufferReader(null))
            : Runtime(gpu, recorders, Set(PackageInstance(), PackageInstance() with { Name = Inactive },
                Instance("main", reads: [new(Producer: PackageView), new(Producer: Inactive, PreviousFrame: (edge == "previous"))])),
                "main", null!, null!, Graph(ScreensGraph(false, "view"), ("view", ((edge == "prepared") ? Inactive : PackageView)))));
        var footprints = new List<RenderGraphFootprint>();

        if (!buffer) {
            footprints.Add(item: new(Consumer: "main", Height: 1, Producer: PackageView, Width: 1));
            if (edge != "absent") { footprints.Add(item: new(Consumer: "main", Height: 1, Producer: Inactive, Width: ((edge == "zero") ? 0 : 1))); }
        }
        var frames = new Frames(runtime, [new(Height: 1, Instance: "main", Width: 1)], footprints);

        if (edge == "previous") {
            // The previous image is a real completed publication, then stands without current producer demand.
            new Frames(runtime, [new(Height: 1, Instance: "main", Width: 1), new(Height: 1, Instance: Inactive, Width: 1)], footprints).Settle();
        }
        frames.Settle();
        if (buffer) { runtime.Node(instance: 0).Paused = true; }
        var request = new FrameCaptureRequest(CaptureRequest().Path);

        runtime.RequestCapture(request: request);
        if (edge == "prepared") {
            // World clears its live placements before producing a frame, then its package composes them again.
            // The captured image still depends on that prepared view, even on the first frozen frame.
            footprints.RemoveAll(match: item => (item.Producer == Inactive));
            package.PrepareFootprints = () => {
                if (!footprints.Any(predicate: item => (item.Producer == Inactive))) {
                    footprints.Add(item: new(Consumer: "main", Height: 1, Producer: Inactive, Width: 1));
                }
            };
        }
        var demanded = (buffer || (edge is "image" or "previous" or "removed" or "prepared"));

        if (demanded) {
            frames.Next(count: 3);
            Assert.False(condition: request.Completion.IsCompleted);
            Assert.Contains("pending demanded source", runtime.UnservedCaptureReason);
            Assert.Single(collection: package.Started, predicate: name => (name == (buffer ? "pool" : Inactive)));
            // A standing buffer or previous image still owns its fence. Repeated demand never restarts that operation.
            if (edge == "removed") { footprints.RemoveAll(match: item => (item.Producer == Inactive)); } else { package.Ready = true; }
        } else {
            Assert.DoesNotContain(Inactive, package.Started);
            Assert.Equal(0UL, runtime.NodeOf(instance: Inactive)!.FrameCounter);
        }
        TestLiveness.Within(frames: 64, step: () => { frames.Next(); return request.Completion.IsCompleted; },
            building: () => runtime.Instances.Instances.Any(predicate: instance => (runtime.NodeOf(instance: instance.Name)?.IsBuildingCandidate == true)),
            reason: () => runtime.UnservedCaptureReason);
        Assert.Null(@object: Outcome(request: request).Error);
        if (demanded) { Assert.Single(collection: package.Started, predicate: name => (name == (buffer ? "pool" : Inactive))); }
    }

    private sealed class CaptureDemandPackage(bool buffer) : IRenderGraphPackageFactory {
        private readonly IRenderGraphPackageFactory m_inner = (buffer ? new NamedBufferPackage() : new ViewPackage());

        public List<string> Started { get; } = [];

        public Action? PrepareFootprints { get; set; }
        public bool Ready { get; set; }

        public void BeginFrame(in FrameContext context) {
            m_inner.BeginFrame(context: in context);
            PrepareFootprints?.Invoke();
        }
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            m_inner.BuildAsync(cancellationToken: cancellationToken, context: context);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            m_inner.Create(built: built, context: context, groups: groups);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => m_inner.CounterOf(instance: instance);
        public RenderGraphPackageFragment? FragmentOf(string instance) => m_inner.FragmentOf(instance: instance);
        public FrameRender CaptureReadinessOf(string instance) => ((Ready || (!buffer && (instance == PackageView)))
            ? FrameRender.Rendered : FrameRender.Waiting(reason: "pending demanded source"));
        public void BeginConvergence(string instance, RenderGraphConvergence convergence) => Started.Add(item: instance);
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
