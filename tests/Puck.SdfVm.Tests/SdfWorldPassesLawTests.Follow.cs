using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a view that resolves another residency renders it in the very frame it resolves it when its
/// passes can record it as built: the residency's tables build in that frame from the pipelines the first one already
/// holds, share every layout the passes were built against, and are sized for the same instance count. The view's
/// counter stays where it was, so no pass rebuilds and no frame is held; that is what lets a seat's view show the
/// world it crosses into from the first frame after it arrives. A residency sized for more instances moves the counter,
/// and the view holds its last image while its passes rebuild against it.
/// </summary>
public sealed partial class SdfWorldPassesLawTests {
    private const int FollowedInstances = 8;

    [InlineData(FollowedInstances, true, false)]
    [InlineData(FollowedInstances, true, true)]
    [InlineData((FollowedInstances * 8), false, false)]
    [InlineData((FollowedInstances * 8), false, true)]
    [Theory]
    public void AViewFollowsAnotherResidencyInPlaceWhenItsPassesCanRecordIt(int otherInstances, bool followsInPlace, bool capture) {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var first = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            instanceCapacity: FollowedInstances,
            kernels: SdfTestPipelines.Kernels(),
            name: "first",
            pipelines: pipelines,
            width: Extent
        );
        var otherFrame = Frame() with { EnableCadenceGate = true };

        otherFrame = otherFrame with { Views = [otherFrame.Views[0], otherFrame.Views[0]] };
        using var other = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: otherFrame),
            height: Extent,
            instanceCapacity: otherInstances,
            kernels: SdfTestPipelines.Kernels(),
            name: "other",
            pipelines: pipelines,
            width: Extent
        );
        var resolved = first;
        var resolvedView = 0;
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(
            Residency: resolved,
            View: resolvedView
        ));
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(
            factory: passes,
            package: RenderGraphPackageCatalog.SdfWorld
        );
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: SdfTestView.Instance,
                Passes: SdfWorldPackage.Fragment.Passes.Count,
                Reads: [],
                Refresh: RenderGraphRefresh.EveryFrame
            )],
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: gpu,
            graphs: new RenderGraphRuntimeGraph?[1],
            hostsOnDirectX: false,
            packages: packages,
            pipelines: pipelines.Pipelines,
            refusal: out var refusal,
            root: SdfTestView.Instance,
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        using var owned = runtime;
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );
        var index = 0L;

        void Produce() {
            var frame = new RenderGraphFrame(
                DisplayHeight: ((int)Extent),
                DisplayHertz: 60,
                DisplayWidth: ((int)Extent),
                Footprints: [],
                Index: index,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: SdfTestView.Instance, Width: 1.0)],
                Tick: index
            );

            index++;
            _ = owned.ProduceFrame(
                context: in context,
                frame: in frame
            );
        }

        SdfTestPipelines.ProduceUntil(
            frame: () => {
                Produce();

                return (first.IsReady && passes.HasRenderedResolvedView(instance: SdfTestView.Instance) &&
                    (owned.Node(instance: 0).Extent == (Extent, Extent)) && !owned.Node(instance: 0).IsBuildingCandidate);
            },
            reason: () => first.NotReadyReason,
            wait: first.WaitPipelineBuilds
        );

        var counter = passes.CounterOf(instance: SdfTestView.Instance)!.Revision;

        // The other residency has not been prepared, nor asked about, before the frame the view resolves it.
        Assert.Null(@object: other.Tables);

        resolved = other;

        // The creator can retire the source before its recorders follow. Their retains keep it alive until the last
        // part moves or the graph retires, including the skipped mesh, ambient and shadow parts.
        first.Dispose();
        Assert.False(condition: first.IsReleased);

        using var directory = new TemporaryDirectory(prefix: "puck-follow-");

        if (capture) {
            owned.RequestCapture(request: new FrameCaptureRequest(path: directory.PathOf(name: "crossing.png")));
        }

        // The frame that first resolves the other residency prepares it, and its tables build in that frame from the
        // pipelines the first residency holds. This one frame decides the follow, independently of pool scheduling.
        Produce();

        Assert.NotNull(@object: other.Tables);

        // Passes that follow the other residency in place keep the counter where it was, so nothing rebuilds and no
        // frame is held for it; a residency sized for more instances moves the counter and the passes rebuild.
        Assert.Equal(
            actual: (passes.CounterOf(instance: SdfTestView.Instance)!.Revision == counter),
            expected: followsInPlace
        );
        Assert.Equal(actual: passes.HasRenderedResolvedView(instance: SdfTestView.Instance), expected: followsInPlace);
        // Either way the view goes on to render the other residency.
        SdfTestPipelines.ProduceUntil(
            frame: () => {
                Produce();

                return passes.HasRenderedResolvedView(instance: SdfTestView.Instance);
            },
            reason: () => other.NotReadyReason,
            wait: other.WaitPipelineBuilds
        );
        Assert.Equal(
            actual: (passes.CounterOf(instance: SdfTestView.Instance)!.Revision == counter),
            expected: followsInPlace
        );

        // A view index change must invalidate this instance's rendered binding even if another view already rendered
        // the destination signature. Rendering the new index restores cadence without rebuilding the passes.
        other.MarkRendered(view: 1);
        var beforeView = passes.CounterOf(instance: SdfTestView.Instance)!.Revision;

        resolvedView = 1;
        passes.BeginFrame(context: in context);
        Assert.False(condition: passes.HasRenderedResolvedView(instance: SdfTestView.Instance));
        Assert.False(condition: passes.IsUnchanged(context: in context, instance: SdfTestView.Instance));
        Assert.Equal(actual: passes.CounterOf(instance: SdfTestView.Instance)!.Revision, expected: beforeView);
        Produce();
        Assert.True(condition: passes.HasRenderedResolvedView(instance: SdfTestView.Instance));

        // A followed residency still drives the counter when its program grows, so the node can replace its scratch.
        var grown = new SdfProgramBuilder();
        var material = grown.AddMaterial(material: new SdfMaterial(Albedo: System.Numerics.Vector3.One));

        for (var instance = 0; (instance < (otherInstances + 1)); instance++) {
            grown.BeginInstance(boundCenter: System.Numerics.Vector3.Zero, boundRadius: 1f);
            grown.Sphere(material: material, radius: 1f);
            grown.EndInstance();
        }

        var beforeGrowth = passes.CounterOf(instance: SdfTestView.Instance)!.Revision;
        var program = grown.Build();

        Assert.True(condition: (program.Instances.Count > otherInstances));
        other.Tables!.UploadProgram(program: program);
        Assert.NotEqual(actual: passes.CounterOf(instance: SdfTestView.Instance)!.Revision, expected: beforeGrowth);
        Assert.Equal(actual: passes.CounterOf(instance: SdfTestView.Instance)!.CountsAt(height: Extent, width: Extent), expected: other.CountsAt(height: Extent, width: Extent));

        owned.Dispose();
        Assert.True(condition: first.IsReleased);
        Assert.False(condition: other.IsReleased);
    }
}
