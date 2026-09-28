using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void AFailedSynchronousLeaseAcquireReleasesEveryLeaseItTook() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            kernels: SdfTestPipelines.Kernels().With(bytecode: ReadOnlyMemory<byte>.Empty, kernel: SdfKernel.Shadow),
            name: "refused-leases",
            pipelines: pipelines,
            width: Extent
        );
        var context = new FrameContext(
            AccumulatorTicks: 0UL, DeltaTicks: 0UL, ElapsedTicks: 0UL, FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0UL, TargetHeight: Extent, TargetWidth: Extent
        );

        residency.BeginFrame();
        Assert.False(condition: residency.Prepare(context: in context));
        Assert.Contains(expectedSubstring: "sdf-world-shadow", actualString: residency.NotReadyReason);
        Assert.Equal(expected: 0, actual: pipelines.Pipelines.SharedPipelines);
        residency.OnDeviceLost();
        residency.BeginFrame();
        Assert.False(condition: residency.Prepare(context: in context));
        Assert.Equal(expected: 0, actual: pipelines.Pipelines.SharedPipelines);
    }
    [Fact]
    public void AReleasedResidencyCannotBeRetainedOrResolvedAgain() {
        var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "released",
            pipelines: SdfTestPipelines.Cache(),
            width: Extent
        );
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        var counter = passes.CounterOf(instance: "view")!;
        var revision = counter.Revision;

        residency.Dispose();
        Assert.True(condition: residency.IsReleased);
        Assert.Throws<ObjectDisposedException>(testCode: residency.Retain);
        Assert.Throws<ObjectDisposedException>(testCode: residency.Retain);
        var context = default(FrameContext);

        passes.BeginFrame(context: in context);
        Assert.NotEqual(expected: revision, actual: counter.Revision);
        Assert.False(condition: passes.IsUnchanged(context: in context, instance: "view"));
        Assert.False(condition: passes.HasRenderedResolvedView(instance: "view"));
    }
}
