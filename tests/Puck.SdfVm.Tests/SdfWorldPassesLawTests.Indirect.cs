using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
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
