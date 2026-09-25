using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="GpuRegionCopyPipelineCache"/> over <see cref="UploadModelGpu"/>, which runs the region-copy
/// kernel's copies: leases on one device share one pipeline, created once and counted once in the cache's ledger, and
/// another device has its own; the last release disposes it and a later lease builds anew; and two regions copying
/// through the one leased pipeline read their contents byte-exact under every residency policy.
/// </summary>
public sealed class GpuRegionCopyPipelineCacheLawTests {
    [Fact]
    public void LeasesOnOneDeviceShareOnePipelineCreatedOnce() {
        var gpu = new UploadModelGpu(reportVersion: 0);
        var other = new UploadModelGpu(reportVersion: 0);
        var cache = new GpuRegionCopyPipelineCache(kernel: new byte[] { UploadModelGpu.RegionCopyBytecode });
        var first = cache.Acquire(device: gpu);
        var second = cache.Acquire(device: gpu);
        var elsewhere = cache.Acquire(device: other);
        var pipeline = Ready(lease: first);

        Assert.Same(expected: pipeline, actual: Ready(lease: second));
        Assert.NotSame(expected: pipeline, actual: Ready(lease: elsewhere));
        Assert.Equal(expected: 2, actual: cache.LeasedDevices);
        Assert.Equal(expected: (2L, 2L), actual: (Read(kind: GpuWork.PipelinesCreated, source: cache.Work), Read(kind: GpuWork.ShaderModulesCreated, source: cache.Work)));

        first.Release();
        first.Release();
        Assert.Null(@object: first.Current);
        Assert.Same(expected: pipeline, actual: second.Poll());
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => first.Poll());

        second.Release();
        elsewhere.Release();
        Assert.Equal(expected: 0, actual: cache.LeasedDevices);

        var later = cache.Acquire(device: gpu);

        Assert.NotSame(expected: pipeline, actual: Ready(lease: later));
        Assert.Equal(expected: 3L, actual: Read(kind: GpuWork.PipelinesCreated, source: cache.Work));
        later.Release();
    }
    [InlineData(GpuResidencyPolicy.InPlace)]
    [InlineData(GpuResidencyPolicy.Ring)]
    [InlineData(GpuResidencyPolicy.Staged)]
    [Theory]
    public void TwoRegionsCopyingThroughTheLeasedPipelineReadTheirContentsExactly(GpuResidencyPolicy policy) {
        const int Slots = 3;
        var gpu = new UploadModelGpu(reportVersion: 0);
        var cache = new GpuRegionCopyPipelineCache(kernel: new byte[] { UploadModelGpu.RegionCopyBytecode });
        var firstLease = cache.Acquire(device: gpu);
        var secondLease = cache.Acquire(device: gpu);
        var random = new Random(Seed: 11);

        using var first = Region(byteCount: 4096, gpu: gpu, pipeline: Ready(lease: firstLease), policy: policy, slots: Slots);
        using var second = Region(byteCount: 1024, gpu: gpu, pipeline: Ready(lease: secondLease), policy: policy, slots: Slots);

        for (var frame = 0; (frame < 12); frame++) {
            var slot = (frame % Slots);

            foreach (var region in ((GpuRegion[])[first, second])) {
                var bytes = new byte[random.Next(maxValue: 96, minValue: 1)];

                random.NextBytes(buffer: bytes);
                _ = region.Write(
                    bytes: bytes,
                    offset: random.Next(maxValue: (region.ByteCount - bytes.Length))
                );
                region.Flush(slot: slot);
                region.RecordCopy(
                    commandBuffer: 2,
                    slot: slot
                );
                Assert.Equal(
                    expected: region.Contents.ToArray(),
                    actual: gpu.Memory(bufferHandle: region.Buffer(slot: slot).BufferHandle)[..region.ByteCount]
                );
            }
        }

        Assert.Equal(
            expected: (policy == GpuResidencyPolicy.Staged),
            actual: (gpu.UploadCopies > 0)
        );
        Assert.Equal(expected: 1L, actual: Read(kind: GpuWork.PipelinesCreated, source: cache.Work));
        firstLease.Release();
        secondLease.Release();
    }

    private static long Read(WorkKind kind, IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }
    // Polls until the lease's pipeline has built on the thread pool. The bound is liveness for a build over a fake
    // device.
    private static GpuRegionCopyPipeline Ready(GpuRegionCopyPipelineLease lease) {
        GpuRegionCopyPipeline? pipeline = null;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => ((pipeline = lease.Poll()) is not null),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return pipeline!;
    }
    private static GpuRegion Region(UploadModelGpu gpu, GpuRegionCopyPipeline pipeline, GpuResidencyPolicy policy, int byteCount, int slots) =>
        new(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: byteCount,
            copyPipeline: pipeline,
            policy: policy,
            recorder: gpu.Services.Recorder,
            slotCount: slots,
            name: default
        );
}
