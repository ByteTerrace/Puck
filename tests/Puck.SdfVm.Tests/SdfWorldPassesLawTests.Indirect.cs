using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void RetiringIndirectCachesRemainInTheResidencyBudgetUntilTheLastReaderReleasesThem() {
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(Frame()), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-budget", pipelines: SdfTestPipelines.Cache(), width: Extent) {
            IndirectTierOverride = SdfIndirectTier.Medium,
        };
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context); }, reason: () => residency.NotReadyReason,
            wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var held = tables.Indirect!.Retain();
        var bytes = held.Bytes;

        Assert.Equal(bytes, tables.IndirectBytes);
        residency.IndirectTierOverride = SdfIndirectTier.Off;
        residency.BeginFrame();
        Assert.True(residency.Prepare(context));
        Assert.Null(tables.Indirect);
        Assert.Equal(bytes, tables.IndirectBytes);
        held.Dispose();
        Assert.Equal(default(GpuMemoryBytes), tables.IndirectBytes);
    }
}
