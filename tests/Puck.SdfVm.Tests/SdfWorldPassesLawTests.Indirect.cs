using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void AChangedFarDistancePublishesAndSubmitsTheReplacementCacheAtTheSameTier() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        using var indirect = new SdfIndirectPasses(views: views);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        // Capture the enabled tier before the runtime chooses its initial fragments.
        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: [
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new RenderGraphRead(Producer: residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)],
                Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 3, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], refusal: out var setRefusal, set: out var set), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(deviceContext: gpu, graphs: new RenderGraphRuntimeGraph?[2],
            hostsOnDirectX: false, packages: packages, pipelines: pipelines.Pipelines, refusal: out var refusal,
            root: "world", runtime: out var runtime, set: set), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;

        void Produce() {
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new RenderGraphRoot(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
        }
        bool Building() => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate);

        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return ((residency.Tables!.Indirect!.Frame > 1) && (graph.Render.Completion == FrameCompletion.Rendered));
        }, building: Building, reason: () => graph.Render.Reason);
        var original = residency.Tables!.Indirect!;
        var originalFragment = indirect.FragmentOf(instance: residency.IndirectInstanceName);
        var originalFrame = original.Frame;

        source = source with { FarDistance = 24f };
        Produce();
        var replacement = residency.Tables.Indirect!;

        Assert.NotSame(actual: replacement, expected: original);
        Assert.Equal(expected: original.Buffer.SizeBytes, actual: replacement.Buffer.SizeBytes);
        Assert.Equal(expected: 24f, actual: replacement.FarDistance);
        Assert.Same(expected: originalFragment, actual: indirect.FragmentOf(instance: residency.IndirectInstanceName));
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return ((replacement.Frame > 1) && (graph.Render.Completion == FrameCompletion.Rendered));
        }, building: Building, reason: () => $"Replacement cache submission {replacement.Frame}; {graph.Render.Reason}");
        Assert.Equal(expected: originalFrame, actual: original.Frame);
    }
    [Fact]
    public void TierChangesRetainOnlyCachesWhoseReadersStillHoldThemWithoutBudgetReads() {
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-retirement", pipelines: SdfTestPipelines.Cache(), width: Extent) {
            IndirectTierOverride = SdfIndirectTier.Medium,
        };
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var held = tables.Indirect!.Retain();

        void Prepare(SdfIndirectTier tier) {
            residency.IndirectTierOverride = tier;
            residency.BeginFrame();
            Assert.True(condition: residency.Prepare(context: context));
        }

        try {
            Prepare(tier: SdfIndirectTier.Off);
            Assert.Equal(1, tables.RetiringIndirectCacheCount);
            for (var replacement = 0; (replacement < 32); replacement++) {
                Prepare(tier: SdfIndirectTier.Medium);
                Prepare(tier: SdfIndirectTier.Off);
                Assert.Equal(1, tables.RetiringIndirectCacheCount);
            }
        } finally {
            held.Dispose();
        }
        Prepare(tier: SdfIndirectTier.Off);
        Assert.Equal(0, tables.RetiringIndirectCacheCount);
        for (var replacement = 0; (replacement < 32); replacement++) {
            Prepare(tier: SdfIndirectTier.High);
            Prepare(tier: SdfIndirectTier.Off);
            Assert.Equal(0, tables.RetiringIndirectCacheCount);
        }
    }
    [Fact]
    public void RetiringIndirectCachesRemainInTheResidencyBudgetUntilTheLastReaderReleasesThem() {
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-budget", pipelines: SdfTestPipelines.Cache(), width: Extent) {
            IndirectTierOverride = SdfIndirectTier.Medium,
        };
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); }, reason: () => residency.NotReadyReason,
            wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var held = tables.Indirect!.Retain();
        var bytes = held.Bytes;

        Assert.Equal(bytes, tables.IndirectBytes);
        residency.IndirectTierOverride = SdfIndirectTier.Off;
        residency.BeginFrame();
        Assert.True(condition: residency.Prepare(context: context));
        Assert.Null(@object: tables.Indirect);
        Assert.Equal(bytes, tables.IndirectBytes);
        held.Dispose();
        Assert.Equal(default(GpuMemoryBytes), tables.IndirectBytes);
    }
}
