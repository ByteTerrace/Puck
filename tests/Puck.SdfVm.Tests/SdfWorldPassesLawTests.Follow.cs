using Puck.Abstractions.Gpu;
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

    [InlineData(FollowedInstances, true)]
    [InlineData((FollowedInstances * 8), false)]
    [Theory]
    public void AViewFollowsAnotherResidencyInPlaceWhenItsPassesCanRecordIt(int otherInstances, bool followsInPlace) {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var pipelines = SdfTestPipelines.Cache();
        using var first = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            instanceCapacity: FollowedInstances,
            kernels: SdfTestPipelines.Kernels(),
            name: "first",
            pipelines: pipelines,
            width: Extent
        );
        using var other = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            instanceCapacity: otherInstances,
            kernels: SdfTestPipelines.Kernels(),
            name: "other",
            pipelines: pipelines,
            width: Extent
        );
        var resolved = first;
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(
            Residency: resolved,
            View: 0
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

        // The bound is liveness for a build over a fake device; it decides nothing.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                Produce();

                return (first.IsReady && passes.HasRenderedResolvedView(instance: SdfTestView.Instance));
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ), userMessage: first.NotReadyReason);

        var counter = passes.CounterOf(instance: SdfTestView.Instance)!.Revision;

        // The other residency has not been prepared, nor asked about, before the frame the view resolves it.
        Assert.Null(@object: other.Tables);

        resolved = other;

        // The frame that first resolves the other residency prepares it, and its tables build in that frame from the
        // pipelines the first residency holds. A frame the runtime defers asks about nothing, so the frames are counted
        // until one resolves it.
        var frames = 0;

        while (other.Frame is null) {
            Produce();

            Assert.True(
                condition: (++frames <= 30),
                userMessage: "the view never resolved the other residency"
            );
        }

        Assert.NotNull(@object: other.Tables);

        // Passes that follow the other residency in place keep the counter where it was, so nothing rebuilds and no
        // frame is held for it; a residency sized for more instances moves the counter and the passes rebuild.
        Assert.Equal(
            actual: (passes.CounterOf(instance: SdfTestView.Instance)!.Revision == counter),
            expected: followsInPlace
        );
        // Either way the view goes on to render the other residency.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                Produce();

                return passes.HasRenderedResolvedView(instance: SdfTestView.Instance);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.Equal(
            actual: (passes.CounterOf(instance: SdfTestView.Instance)!.Revision == counter),
            expected: followsInPlace
        );
    }
}
