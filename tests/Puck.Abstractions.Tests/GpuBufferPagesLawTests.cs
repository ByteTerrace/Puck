using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

public sealed class GpuBufferPagesLawTests {
    private const int Page = GpuBufferPages.MinimumPageBytes;

    [Fact]
    public void EveryPagedReplacementUsesItsOwnAuthorityAndExternalLeasesUseTheTargetOffset() {
        var authorities = new List<GpuBufferPages>();
        var gpu = new UploadModelGpu {
            DestinationPages = (_, size) => {
                var pages = new GpuBufferPages(((long)size), Page);

                authorities.Add(item: pages);
                return pages;
            },
        };
        using var module = gpu.Services.ShaderModuleFactory.Create(bytecode: new byte[] { UploadModelGpu.RegionCopyBytecode }, stage: GpuShaderStage.Compute);
        using var pipeline = gpu.Services.PipelineFactory.Create(computeShaderModule: module, description: GpuRegion.CopyPipeline, name: default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, Page, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);

        Copy(gpu: gpu, region: region);
        var old = region.TryLease(bytes: 4, offset: 0)!.Value;
        using var grownFrom = region.Grow(byteCount: (Page * 2));

        Assert.False(condition: old.IsCurrent);
        Copy(gpu: gpu, region: region);
        authorities[^1].SetWritable(count: 1, firstPage: 1, writable: false);
        Assert.Null(value: region.TryLease(bytes: 4, offset: Page));
        old = region.TryLease(bytes: 4, offset: 0)!.Value;
        using var shrunkFrom = region.Shrink(byteCount: Page);

        Assert.False(condition: old.IsCurrent);
        Assert.NotNull(value: region.TryLease(bytes: 4, offset: 0));

        using var target = gpu.Services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: (Page * 3), usage: GpuBufferUsage.Storage);
        using var external = new GpuRegion(target, Page, 1, gpu.Services.BufferFactory, gpu.Services.Bindings,
            gpu.Services.Recorder, pipeline, default);

        Copy(gpu: gpu, region: external);
        external.Target(destinationWord: (Page / 4));
        authorities[^1].SetWritable(count: 1, firstPage: 1, writable: false);
        Assert.Null(value: external.TryLease(bytes: 4, offset: 0));
        authorities[^1].SetWritable(count: 1, firstPage: 1, writable: true);
        var lease = external.TryLease(bytes: 4, offset: 0)!.Value;

        authorities[^1].SetWritable(count: 1, firstPage: 0, writable: false);
        Assert.True(condition: lease.IsCurrent);
    }
    [Fact]
    public void ALeaseNeverSurvivesRevocationAndRemappingButUnrelatedPagesDo() {
        var pages = new GpuBufferPages((Page * 3), Page);
        var left = pages.TryLease(bytes: Page, offset: 0)!.Value;
        var middle = pages.TryLease(bytes: Page, offset: Page)!.Value;
        var crossing = pages.TryLease(bytes: 8, offset: (Page - 4))!.Value;

        pages.SetWritable(count: 1, firstPage: 1, writable: false);
        Assert.Null(value: pages.TryLease(bytes: 8, offset: (Page - 4)));
        Assert.False(condition: middle.IsCurrent);
        Assert.False(condition: crossing.IsCurrent);
        Assert.True(condition: left.IsCurrent);
        pages.SetWritable(count: 1, firstPage: 1, writable: true);
        Assert.False(condition: middle.IsCurrent);
        Assert.False(condition: crossing.IsCurrent);
        Assert.True(condition: pages.TryLease(bytes: 8, offset: (Page - 4))!.Value.IsCurrent);
        Assert.True(condition: left.IsCurrent);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CoalescedAndPairedRunsNeverWriteAnUnleasedPage(bool overflowRuns) {
        var count = (overflowRuns ? 600 : 4);
        var bytes = (count * Page);
        var pages = new GpuBufferPages(bytes, Page);
        var gpu = new UploadModelGpu { DestinationPages = (_, _) => pages };
        using var module = gpu.Services.ShaderModuleFactory.Create(bytecode: new byte[] { UploadModelGpu.RegionCopyBytecode }, stage: GpuShaderStage.Compute);
        using var pipeline = gpu.Services.PipelineFactory.Create(computeShaderModule: module, description: GpuRegion.CopyPipeline, name: default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, bytes, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);

        Copy(gpu: gpu, region: region);
        for (var page = 1; (page < count); page += 2) { pages.SetWritable(count: 1, firstPage: page, writable: false); }
        var memory = gpu.DeviceLocal(((ulong)bytes));

        Array.Fill(array: memory, value: ((byte)0xCC));
        if (overflowRuns) {
            for (var page = 0; (page < count); page += 2) {
                region.Write(bytes: new byte[] { 1, 2, 3, 4 }, offset: (page * Page));
            }
        } else {
            // These writes merge across a two-word gap which straddles the revoked page's boundary.
            region.Write(bytes: new byte[] { 1, 2, 3, 4 }, offset: (Page - 4));
            region.Write(bytes: new byte[] { 5, 6, 7, 8 }, offset: (Page + 8));
            region.Write(bytes: new byte[] { 9, 10, 11, 12 }, offset: (Page * 2));
        }
        Copy(gpu: gpu, region: region);
        for (var page = 1; (page < count); page += 2) {
            Assert.All(memory.AsSpan(length: Page, start: (page * Page)).ToArray(), value => Assert.Equal(actual: value, expected: 0xCC));
        }
        Assert.Equal((overflowRuns ? (byte)1 : (byte)9), memory[(overflowRuns ? 0 : (Page * 2))]);
    }
    [Fact]
    public void RevocationBetweenStagingAndRecordingRestagesAndRetainsUnmappedDebt() {
        var pages = new GpuBufferPages(Page, Page);
        var gpu = new UploadModelGpu { DestinationPages = (_, _) => pages };
        using var module = gpu.Services.ShaderModuleFactory.Create(bytecode: new byte[] { UploadModelGpu.RegionCopyBytecode }, stage: GpuShaderStage.Compute);
        using var pipeline = gpu.Services.PipelineFactory.Create(computeShaderModule: module, description: GpuRegion.CopyPipeline, name: default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, Page, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);

        region.Flush(slot: 0);
        pages.SetWritable(count: 1, firstPage: 0, writable: false);
        region.RecordCopy(1, 0);
        Assert.True(condition: region.OwesCopy);
        pages.SetWritable(count: 1, firstPage: 0, writable: true);
        Copy(gpu: gpu, region: region);
        Assert.False(condition: region.OwesCopy);
        region.Write(bytes: new byte[] { 7, 8, 9, 10 }, offset: 0);
        region.Flush(slot: 0);
        pages.SetWritable(count: 1, firstPage: 0, writable: false);
        pages.SetWritable(count: 1, firstPage: 0, writable: true);
        gpu.Services.Recorder.MemoryBarrier(commandBufferHandle: 1, destinationAccessMask: GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader, sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader);
        region.RecordCopy(1, 0);
        Assert.Equal(7, gpu.DeviceLocal(Page)[0]);
    }

    private static void Copy(UploadModelGpu gpu, GpuRegion region) {
        var recording = new GpuRegionCopyRecording(gpu.Services.Recorder, GpuStage.ComputeShader, static () => 2);

        recording.Record(region, 0, handsToReaders: true);
        recording.Finish();
    }
}
