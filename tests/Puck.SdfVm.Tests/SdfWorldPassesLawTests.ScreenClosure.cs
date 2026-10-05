using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void OwnWorldAndUnrelatedCamerasDoNotEnterScreenLightingClosure() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { IndirectTier = SdfIndirectTier.Off };
        var captures = 0;
        using var own = new SdfWorldResidency(pipelines, new FixedFrameSource(frame: source), SdfTestPipelines.Kernels(),
            "own", Extent, Extent, screenSources: new ClosureScreens("own"), brickPoolVoxelCapacity: 0);
        using var unrelated = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { captures++; return source; }),
            SdfTestPipelines.Kernels(), "unrelated", Extent, Extent, brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: ((name == "own") ? own : unrelated), View: 0));
        using var environment = new SdfSkyEnvironmentPasses(views: views);

        environment.Register(name: "own-env", residency: own, view: 0);
        environment.Register(name: "unrelated-env", residency: unrelated, view: 0);
        views.ObserveViews(instances: ["own", "unrelated"]);
        _ = views.ViewOf(instance: "own");
        _ = views.ViewOf(instance: "unrelated");
        var context = ContextOf(gpu: gpu);

        own.ProduceFirstFrame(context: context);
        unrelated.ProduceFirstFrame(context: context);
        var before = captures;

        for (var frame = 0; (frame < 4); frame++) {
            views.BeginFrame(context: context);
            Assert.True(condition: views.CaptureReadinessOf(instance: "own").IsRendered);
            Assert.True(condition: views.CaptureReadinessOf(instance: "unrelated").IsRendered);
        }
        Assert.Equal(actual: captures, expected: (before + 4));
    }
    [Fact]
    public void AColdScreenClosureRefusesAFrozenParticipantWithoutResettingItsCache() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { IndirectTier = SdfIndirectTier.Medium, FarDistance = 12f };
        using var left = new SdfWorldResidency(pipelines, new FixedFrameSource(frame: source), SdfTestPipelines.Kernels(),
            "left", Extent, Extent, screenSources: new ClosureScreens("right"), brickPoolVoxelCapacity: 0);
        using var right = new SdfWorldResidency(pipelines, new FixedFrameSource(frame: source with { IndirectTier = SdfIndirectTier.Off }),
            SdfTestPipelines.Kernels(), "right", Extent, Extent, brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: ((name == "left") ? left : right), View: 0));
        using var environment = new SdfSkyEnvironmentPasses(views: views);

        environment.Register(name: "left-env", residency: left, view: 0);
        environment.Register(name: "right-env", residency: right, view: 0);
        views.ObserveViews(instances: ["left", "right"]);
        _ = views.ViewOf(instance: "left");
        _ = views.ViewOf(instance: "right");
        var context = ContextOf(gpu: gpu);

        left.ProduceFirstFrame(context: context);
        right.ProduceFirstFrame(context: context);
        var cache = left.Tables!.Indirect!;
        var history = cache.History;

        left.IndirectFrozen = true;
        views.BeginConvergence("left", new RenderGraphConvergence(request: new FrameCaptureRequest("unused-frozen-closure.png")));
        views.BeginFrame(context: context);
        Assert.Equal(FrameCompletion.Refused, views.CaptureReadinessOf(instance: "left").Completion);
        Assert.Equal(history, cache.History);
        Assert.Same(cache, left.Tables.Indirect);
    }
    [InlineData(SdfIndirectTier.Off, false)]
    [InlineData(SdfIndirectTier.Medium, false)]
    [InlineData(SdfIndirectTier.Off, true)]
    [Theory]
    public void MutualScreenLightingClosesTwiceAndDerivedFramesDoNotRestartIt(SdfIndirectTier tier, bool taintedPanorama) =>
        CompleteClosure(tier, taintedPanorama, derivedPanorama: false);
    [InlineData(SdfIndirectTier.Off)]
    [InlineData(SdfIndirectTier.Medium)]
    [Theory]
    public void WorldDerivedPanoramasJoinTwoFencedLightingRounds(SdfIndirectTier tier) =>
        CompleteClosure(tier, taintedPanorama: false, derivedPanorama: true);

    private static void CompleteClosure(SdfIndirectTier tier, bool taintedPanorama, bool derivedPanorama) {
        var gpu = new FakeGpuDevice(holdFences: true) { DistinctImages = true };
        var pipelines = SdfTestPipelines.Cache();
        var leftFrame = Frame() with { IndirectTier = tier, FarDistance = 12f, IndirectSources = SdfIndirectSources.Screens };

        if (derivedPanorama) {
            leftFrame = leftFrame with { IndirectSources = SdfIndirectSources.Sky };
            _ = leftFrame.Sky.Add(new SdfSkyPanorama { Intensity = 1f, Screen = 0 }, "derived-panorama", visibility: SdfSkyVisibility.Lighting,
                opacity: 0.5f);
        }
        if (taintedPanorama) {
            _ = leftFrame.Sky.Add(new SdfSkyPanorama { Intensity = 1f, Screen = 1 }, "independent-panorama", visibility: SdfSkyVisibility.Lighting);
        }
        // The taint control has one derived edge: the left world sees a clean right camera and independently sees
        // a lighting-only external Panorama. Its screen reduction can stay clean while its sky retains old taint.
        var rightFrame = (taintedPanorama ? Frame() with { IndirectTier = tier, FarDistance = 12f, IndirectSources = SdfIndirectSources.Screens } : leftFrame);
        using var left = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => leftFrame), SdfTestPipelines.Kernels(),
            "left", Extent, Extent, screenSources: new ClosureScreens("right", taintedPanorama, emits: !derivedPanorama), brickPoolVoxelCapacity: 0);
        using var right = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => rightFrame), SdfTestPipelines.Kernels(),
            "right", Extent, Extent, screenSources: new ClosureScreens((taintedPanorama ? null : "left"), emits: !derivedPanorama), brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: ((name == "left") ? left : right), View: 0));

        views.ObserveViews(instances: ["left", "right"]);
        using var environment = new SdfSkyEnvironmentPasses(views: views);

        environment.Register(name: "left-env", residency: left, view: 0);
        environment.Register(name: "right-env", residency: right, view: 0);
        using var indirect = new SdfIndirectPasses(views: views);

        if (tier != SdfIndirectTier.Off) {
            indirect.Register(name: left.IndirectInstanceName, residency: left);
            indirect.Register(name: right.IndirectInstanceName, residency: right);
        }
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        using var panorama = new ClosurePanoramaFeed(gpu: gpu);

        packages.RegisterProducer(factory: _ => panorama, package: "closure-panorama");
        var context = ContextOf(gpu: gpu);

        left.ProduceFirstFrame(context: context);
        right.ProduceFirstFrame(context: context);
        List<RenderGraphInstance> instances = [
            World(environment: "left-env", name: "left", other: "right"), World(environment: "right-env", name: "right", other: "left"),
            Environment(name: "left-env", other: "right"), Environment(name: "right-env", other: "left"),
        ];

        if (tier != SdfIndirectTier.Off) {
            instances.Add(item: Indirect(environment: "left-env", residency: left));
            instances.Add(item: Indirect(environment: "right-env", residency: right));
        }
        var graphCount = instances.Count;

        if (taintedPanorama) {
            instances.Add(item: new(Name: "panorama-feed", ExternalPackage: "closure-panorama", Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        }
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances, out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[instances.Count], "left", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;

        void Produce(bool complete = true) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Tick: frame++, Roots: [new(Height: 1, Instance: "left", Width: 1), new(Height: 1, Instance: "right", Width: 1),
                    .. (taintedPanorama ? new RenderGraphRoot[] { new(Height: 1, Instance: "panorama-feed", Width: 1) } : [])]);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
        }
        void Until(Func<bool> done, bool complete = true) => TestLiveness.Within(frames: 96,
            step: () => { Produce(complete: complete); return done(); },
            building: () => Enumerable.Range(count: graphCount, start: 0).Any(predicate: index => graph.Node(instance: index).IsBuildingCandidate), reason: () => graph.Render.Reason);

        Until(() => ((left.Tables?.SubmittedScreenEmission is not null) && (right.Tables?.SubmittedScreenEmission is not null)), complete: false);
        Assert.False(condition: views.CaptureReadinessOf(instance: "left").IsRendered);
        Assert.False(condition: left.Tables!.CompletedScreenEmission.IsKnown);
        Until(() => ((Sequence(residency: left) == 2) && (Sequence(residency: right) == (taintedPanorama ? 1 : 2))));
        Until(() => ((Sequence(residency: left) == 3) && (Sequence(residency: right) == RightSequence(epoch: 1))));
        foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
        Produce(complete: false);
        // Even Off must render the final reduced source into its destination image. Its unsignaled image submission
        // cannot complete the epoch merely because the second reduction's fence was already signaled.
        Assert.False(condition: views.CaptureReadinessOf(instance: "left").IsRendered);
        Until(() => views.CaptureReadinessOf(instance: "left").IsRendered);
        Assert.Equal(RightSequence(epoch: 1), Sequence(residency: right));
        Assert.NotNull(@object: right.Tables);
        if (tier == SdfIndirectTier.Off) {
            Assert.Null(@object: left.Tables.Indirect);
            Assert.Null(@object: right.Tables.Indirect);
        } else {
            Assert.True(condition: left.IsIndirectReady);
            Assert.True(condition: right.IsIndirectReady);
            if (!derivedPanorama) {
                Assert.Equal(left.Tables.SubmittedScreenEmission!.Value.Publication, left.Tables.Indirect!.PublishedLightingSource!.Screens);
                Assert.Equal(right.Tables!.SubmittedScreenEmission!.Value.Publication, right.Tables.Indirect!.PublishedLightingSource!.Screens);
            }
        }
        var leftPublication = left.Tables.SubmittedScreenEmission!.Value.Publication;
        var rightPublication = right.Tables!.SubmittedScreenEmission!.Value.Publication;
        var leftEnvironment = left.Tables.SubmittedSkyEnvironment!.Value.Publication;
        var rightEnvironment = right.Tables.SubmittedSkyEnvironment!.Value.Publication;

        if (derivedPanorama) {
            Assert.Equal(1, left.Tables.SubmittedScreenEmission.Value.Sequence);
            Assert.Equal(1, right.Tables.SubmittedScreenEmission.Value.Sequence);
            if (tier != SdfIndirectTier.Off) {
                Assert.Equal(leftEnvironment, left.Tables.Indirect!.PublishedLightingSource!.Environment);
                Assert.Equal(rightEnvironment, right.Tables.Indirect!.PublishedLightingSource!.Environment);
            }
        }
        for (var repeat = 0; (repeat < 12); repeat++) { Produce(); }
        Assert.Equal(leftPublication, left.Tables.SubmittedScreenEmission!.Value.Publication);
        Assert.Equal(rightPublication, right.Tables.SubmittedScreenEmission!.Value.Publication);
        Assert.Equal(leftEnvironment, left.Tables.SubmittedSkyEnvironment!.Value.Publication);
        Assert.Equal(rightEnvironment, right.Tables.SubmittedSkyEnvironment!.Value.Publication);

        // An actual camera edit starts one new finite component epoch. Its successive derived images do not.
        var camera = leftFrame.Views[0].Camera;

        leftFrame = leftFrame with {
            Views = [leftFrame.Views[0] with { Camera = new CameraSnapshot(
            (camera.Position + Vector3.UnitX), camera.Right, camera.Up, camera.Forward, camera.TanHalfFieldOfView, camera.AspectRatio) }],
        };
        if (tier != SdfIndirectTier.Off) {
            var cache = left.Tables.Indirect!;
            var heldSource = cache.PublishedLightingSource;
            var history = cache.History;

            left.IndirectFrozen = true;
            Produce();
            Produce();
            Assert.Same(heldSource, cache.PublishedLightingSource);
            Assert.Equal(history, cache.History);
            Assert.Equal(leftPublication, left.Tables.SubmittedScreenEmission!.Value.Publication);
            Assert.Equal(FrameCompletion.Refused, views.CaptureReadinessOf(instance: "left").Completion);
            left.IndirectFrozen = false;
        }
        Until(() => ((Sequence(residency: left) == 6) && views.CaptureReadinessOf(instance: "left").IsRendered));
        Assert.Equal(RightSequence(epoch: 2), Sequence(residency: right));

        // Even a zero-convergence capture starts from a new dark-derived boundary and completes both rounds.
        var capture = new RenderGraphConvergence(request: new FrameCaptureRequest("unused-closure-capture.png"));

        views.BeginConvergence(convergence: capture, instance: "left");
        Produce();
        Assert.False(condition: views.CaptureReadinessOf(instance: "left").IsRendered);
        Until(() => ((Sequence(residency: left) == 9) && views.CaptureReadinessOf(instance: "left").IsRendered));
        Assert.Equal(RightSequence(epoch: 3), Sequence(residency: right));
        if (taintedPanorama) {
            var independent = views.ReadEpochOf(instance: "left", producer: "panorama-feed");

            Assert.NotNull(@object: independent);
            Assert.Same(independent, environment.ReadEpochOf(instance: "left-env", producer: "panorama-feed"));
            Assert.Null(@object: views.ReadEpochOf(instance: "left", producer: "right"));
            Assert.True(condition: left.Tables.SubmittedSkyEnvironment!.Value.Tainted);
            Assert.False(condition: left.Tables.SubmittedScreenEmission!.Value.Tainted);
            var taintedProjection = left.Tables.SubmittedSkyEnvironment.Value.Publication;

            panorama.Filling = true;
            // The capture is still active. A clean screen table cannot hide the older tainted sky projection.
            Until(() => ((left.Tables.SubmittedScreenEmission!.Value.Sequence == 12) && views.CaptureReadinessOf(instance: "left").IsRendered));
            Assert.NotEqual(taintedProjection, left.Tables.SubmittedSkyEnvironment!.Value.Publication);
            Assert.False(condition: left.Tables.SubmittedSkyEnvironment.Value.Tainted);
        }

        long RightSequence(int epoch) => (taintedPanorama ? epoch : (epoch * 3L));
        long Sequence(SdfWorldResidency residency) => (derivedPanorama
            ? (residency.Tables!.SubmittedSkyEnvironment?.Sequence ?? 0) : (residency.Tables!.SubmittedScreenEmission?.Sequence ?? 0));
        RenderGraphInstance World(string name, string other, string environment) => new(Name: name,
            ExternalPackage: RenderGraphPackageCatalog.SdfWorld, Passes: SdfWorldPackage.NativeFragment.Passes.Count,
            Reads: [new(environment, Kind: ShaderPipelineResourceKind.Buffer),
                .. ((taintedPanorama && (name == "right")) ? Array.Empty<RenderGraphRead>() : [new(other, PreviousFrame: true)]),
                .. ((taintedPanorama && (name == "left")) ? new RenderGraphRead[] { new("panorama-feed") } : []),
                .. ((tier == SdfIndirectTier.Off) ? Array.Empty<RenderGraphRead>() :
                    [new RenderGraphRead(((name == "left") ? left : right).IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)])],
            Refresh: RenderGraphRefresh.EveryFrame);
        RenderGraphInstance Indirect(SdfWorldResidency residency, string environment) => new(Name: residency.IndirectInstanceName,
            ExternalPackage: RenderGraphPackageCatalog.Indirect, Passes: indirect.FragmentOf(instance: residency.IndirectInstanceName)!.Passes.Count,
            Output: ShaderPipelineResourceKind.Buffer, Reads: [new(environment, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame);
        RenderGraphInstance Environment(string name, string other) => new(Name: name,
            ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count,
            Output: ShaderPipelineResourceKind.Buffer, Reads: [
                .. ((taintedPanorama && (name == "right-env")) ? Array.Empty<RenderGraphRead>() : [new(other, PreviousFrame: true)]),
                .. ((taintedPanorama && (name == "left-env")) ? new RenderGraphRead[] { new("panorama-feed") } : [])], Refresh: RenderGraphRefresh.EveryFrame);
    }

    private sealed class ClosureScreens(string? producer, bool panorama = false, bool emits = true) : ISdfScreenSources {
        public IReadOnlyList<int> Screens { get; } = (panorama ? [0, 1] : [0]);

        public bool Emits(int screen) => (emits && (screen == 0) && (producer is not null));
        public SourceMapping? MappingOf(int screen) => null;
        public string? ReadOf(int view, int screen) => ((screen == 0) ? producer : "panorama-feed");
    }
    private sealed class ClosurePanoramaFeed(FakeGpuDevice gpu) : IRenderGraphExternalProducer {
        private IGpuImage? m_image;

        public bool Filling { get; set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => ((m_image is null) ? "no panorama image" : null);
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work { get; } = new GpuWorkLedger(framesInFlight: 3, name: "closure.panorama");

        public void Dispose() { m_image?.Dispose(); m_image = null; }
        public void OnDeviceLost() => Dispose();
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_image ??= gpu.Services.ImageFactory.Create(format: Format, height: height, name: default,
                usage: GpuImageUsage.Sampled, width: width);
            return FrameRender.Rendered;
        }
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException(message: "Capture the consuming world."));
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (m_image is not { } image) { output = default; return false; }
            output = new(Surface.SameDeviceImage(image.ImageHandle, image.ImageViewHandle, image.Width, image.Height, Format),
                GpuImageLayout.ShaderReadOnly, new GpuImageLease(image.ImageViewHandle) {
                    Publication = new(Owner: this, Sequence: (Filling ? 2 : 1)),
                }, Tainted: !Filling);
            return true;
        }
    }
}
