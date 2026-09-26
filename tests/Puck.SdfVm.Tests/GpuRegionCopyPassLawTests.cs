using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="GpuRegionCopyPass"/> over <see cref="UploadModelGpu"/>, which runs the region-copy kernel's
/// copies: leases on one device share one pass pipeline, created once and counted once in the pass-pipeline cache's
/// ledger, and another device has its own; the last release disposes it and a later lease builds anew; the deployed
/// kernel is read when the pass is constructed and never by an acquire; and two regions copying through the one leased
/// pipeline read their contents byte-exact under every residency policy.
/// </summary>
public sealed class GpuRegionCopyPassLawTests {
    [Fact]
    public void LeasesOnOneDeviceShareOnePipelineCreatedOnce() {
        var gpu = new UploadModelGpu(reportVersion: 0);
        var other = new UploadModelGpu(reportVersion: 0);
        var cache = new GpuPassPipelineCache();
        var pass = new GpuRegionCopyPass(kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }, pipelines: cache);
        var first = pass.Acquire(device: gpu);
        var second = pass.Acquire(device: gpu);
        var elsewhere = pass.Acquire(device: other);
        var pipeline = Ready(lease: first);

        Assert.Same(expected: pipeline, actual: Ready(lease: second));
        Assert.NotSame(expected: pipeline, actual: Ready(lease: elsewhere));
        Assert.Equal(expected: 2, actual: cache.SharedPipelines);
        Assert.Equal(expected: (2L, 2L), actual: (Read(kind: GpuWork.PipelinesCreated, source: cache.Work), Read(kind: GpuWork.ShaderModulesCreated, source: cache.Work)));

        first.Release();
        first.Release();
        Assert.Null(@object: first.Current);
        Assert.Same(expected: pipeline, actual: second.Poll()!.Compute);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => first.Poll());

        second.Release();
        elsewhere.Release();
        Assert.Equal(expected: 0, actual: cache.SharedPipelines);

        var later = pass.Acquire(device: gpu);

        Assert.NotSame(expected: pipeline, actual: Ready(lease: later));
        Assert.Equal(expected: 3L, actual: Read(kind: GpuWork.PipelinesCreated, source: cache.Work));
        later.Release();
    }
    // The deployed kernel is read when the pass is constructed, at composition, and never by an acquire: a pass over a
    // backend with no deployed kernel fails to construct, and every lease a constructed pass hands out is on the one key
    // it read, so a frame-thread acquire has no file to read.
    [Fact]
    public void ThePassReadsItsDeployedKernelWhenConstructedAndAnAcquireReadsNothing() {
        var cache = new GpuPassPipelineCache();

        _ = Assert.Throws<FileNotFoundException>(testCode: () => new GpuRegionCopyPass(bytecodeExtension: ".absent", pipelines: cache));

        var pass = new GpuRegionCopyPass(bytecodeExtension: ".spv", pipelines: cache);
        var gpu = new UploadModelGpu(reportVersion: 0);

        Assert.Equal(
            actual: pass.Key,
            expected: GpuRegionCopyPass.KeyOf(kernel: GpuRegionCopyPass.Load(bytecodeExtension: ".spv", directory: GpuRegionCopyPass.DefaultDirectory))
        );

        var first = pass.Acquire(device: gpu);
        var second = pass.Acquire(device: gpu);

        Assert.Same(expected: pass.Key, actual: first.Key);
        Assert.Same(expected: pass.Key, actual: second.Key);
        first.Release();
        second.Release();
        Assert.Equal(expected: 0, actual: cache.SharedPipelines);
    }
    [InlineData(GpuResidencyPolicy.InPlace)]
    [InlineData(GpuResidencyPolicy.Ring)]
    [InlineData(GpuResidencyPolicy.Staged)]
    [Theory]
    public void TwoRegionsCopyingThroughTheLeasedPipelineReadTheirContentsExactly(GpuResidencyPolicy policy) {
        const int Slots = 3;
        var gpu = new UploadModelGpu(reportVersion: 0);
        var cache = new GpuPassPipelineCache();
        var pass = new GpuRegionCopyPass(kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }, pipelines: cache);
        var firstLease = pass.Acquire(device: gpu);
        var secondLease = pass.Acquire(device: gpu);
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
                Copy(gpu: gpu, region: region, slot: slot);
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

    // Records one region's owed copy for a slot as its owner does: through the owed-copy recording, into command buffer 2.
    private static void Copy(UploadModelGpu gpu, GpuRegion region, int slot) {
        var recording = new GpuRegionCopyRecording(
            begin: static () => 2,
            readers: GpuStage.ComputeShader,
            recorder: gpu.Services.Recorder
        );

        recording.Record(
            handsToReaders: true,
            region: region,
            slot: slot
        );
        _ = recording.Finish();
    }
    private static long Read(WorkKind kind, IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }
    // Polls until the lease's pipeline has built on the thread pool. The bound is liveness for a build over a fake
    // device.
    private static IGpuComputePipeline Ready(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease) {
        GpuPassPipeline? pipeline = null;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => ((pipeline = lease.Poll()) is not null),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return pipeline!.Compute!;
    }
    private static GpuRegion Region(UploadModelGpu gpu, IGpuComputePipeline pipeline, GpuResidencyPolicy policy, int byteCount, int slots) =>
        new(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: byteCount,
            copyPipeline: pipeline,
            memory: GpuHostVisibleMemory.Host,
            policy: policy,
            recorder: gpu.Services.Recorder,
            slotCount: slots,
            name: default
        );
}
