using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

public sealed class GpuBufferPagesLawTests {
    private const int Page = GpuBufferPages.MinimumPageBytes;

    [Fact]
    public void EveryPagedReplacementUsesItsOwnAuthorityAndExternalLeasesUseTheTargetOffset() {
        var authorities = new List<GpuBufferPages>();
        var gpu = new UploadModelGpu { DestinationPages = (_, size) => {
            var pages = new GpuBufferPages((long)size, Page);
            authorities.Add(pages);
            return pages;
        } };
        using var module = gpu.Services.ShaderModuleFactory.Create(GpuShaderStage.Compute, new byte[] { UploadModelGpu.RegionCopyBytecode });
        using var pipeline = gpu.Services.PipelineFactory.Create(module, GpuRegion.CopyPipeline, default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, Page, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);
        Copy(gpu, region);
        var old = region.TryLease(0, 4)!.Value;
        using var grownFrom = region.Grow(Page * 2);
        Assert.False(old.IsCurrent);
        Copy(gpu, region);
        authorities[^1].SetWritable(1, 1, false);
        Assert.Null(region.TryLease(Page, 4));
        old = region.TryLease(0, 4)!.Value;
        using var shrunkFrom = region.Shrink(Page);
        Assert.False(old.IsCurrent);
        Assert.NotNull(region.TryLease(0, 4));

        using var target = gpu.Services.BufferFactory.CreateDeviceLocal(Page * 3, GpuBufferUsage.Storage, default);
        using var external = new GpuRegion(target, Page, 1, gpu.Services.BufferFactory, gpu.Services.Bindings,
            gpu.Services.Recorder, pipeline, default);
        Copy(gpu, external);
        external.Target(Page / 4);
        authorities[^1].SetWritable(1, 1, false);
        Assert.Null(external.TryLease(0, 4));
        authorities[^1].SetWritable(1, 1, true);
        var lease = external.TryLease(0, 4)!.Value;
        authorities[^1].SetWritable(0, 1, false);
        Assert.True(lease.IsCurrent);
    }

    [Fact]
    public void ALeaseNeverSurvivesRevocationAndRemappingButUnrelatedPagesDo() {
        var pages = new GpuBufferPages(Page * 3, Page);
        var left = pages.TryLease(0, Page)!.Value;
        var middle = pages.TryLease(Page, Page)!.Value;
        var crossing = pages.TryLease(Page - 4, 8)!.Value;
        pages.SetWritable(1, 1, false);
        Assert.Null(pages.TryLease(Page - 4, 8));
        Assert.False(middle.IsCurrent);
        Assert.False(crossing.IsCurrent);
        Assert.True(left.IsCurrent);
        pages.SetWritable(1, 1, true);
        Assert.False(middle.IsCurrent);
        Assert.False(crossing.IsCurrent);
        Assert.True(pages.TryLease(Page - 4, 8)!.Value.IsCurrent);
        Assert.True(left.IsCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoalescedAndPairedRunsNeverWriteAnUnleasedPage(bool overflowRuns) {
        var count = overflowRuns ? 600 : 4;
        var bytes = count * Page;
        var pages = new GpuBufferPages(bytes, Page);
        var gpu = new UploadModelGpu { DestinationPages = (_, _) => pages };
        using var module = gpu.Services.ShaderModuleFactory.Create(GpuShaderStage.Compute, new byte[] { UploadModelGpu.RegionCopyBytecode });
        using var pipeline = gpu.Services.PipelineFactory.Create(module, GpuRegion.CopyPipeline, default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, bytes, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);
        Copy(gpu, region);
        for (var page = 1; page < count; page += 2) { pages.SetWritable(page, 1, false); }
        var memory = gpu.DeviceLocal((ulong)bytes);
        Array.Fill(memory, (byte)0xCC);
        if (overflowRuns) {
            for (var page = 0; page < count; page += 2) {
                region.Write(page * Page, new byte[] { 1, 2, 3, 4 });
            }
        } else {
            // These writes merge across a two-word gap which straddles the revoked page's boundary.
            region.Write(Page - 4, new byte[] { 1, 2, 3, 4 });
            region.Write(Page + 8, new byte[] { 5, 6, 7, 8 });
            region.Write(Page * 2, new byte[] { 9, 10, 11, 12 });
        }
        Copy(gpu, region);
        for (var page = 1; page < count; page += 2) {
            Assert.All(memory.AsSpan(page * Page, Page).ToArray(), value => Assert.Equal(0xCC, value));
        }
        Assert.Equal(overflowRuns ? (byte)1 : (byte)9, memory[overflowRuns ? 0 : Page * 2]);
    }

    [Fact]
    public void RevocationBetweenStagingAndRecordingRestagesAndRetainsUnmappedDebt() {
        var pages = new GpuBufferPages(Page, Page);
        var gpu = new UploadModelGpu { DestinationPages = (_, _) => pages };
        using var module = gpu.Services.ShaderModuleFactory.Create(GpuShaderStage.Compute, new byte[] { UploadModelGpu.RegionCopyBytecode });
        using var pipeline = gpu.Services.PipelineFactory.Create(module, GpuRegion.CopyPipeline, default);
        using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, Page, 1,
            gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline, default);
        region.Flush(0);
        pages.SetWritable(0, 1, false);
        region.RecordCopy(1, 0);
        Assert.True(region.OwesCopy);
        pages.SetWritable(0, 1, true);
        Copy(gpu, region);
        Assert.False(region.OwesCopy);
        region.Write(0, new byte[] { 7, 8, 9, 10 });
        region.Flush(0);
        pages.SetWritable(0, 1, false);
        pages.SetWritable(0, 1, true);
        gpu.Services.Recorder.MemoryBarrier(commandBufferHandle: 1, sourceStageMask: GpuStage.ComputeShader,
            destinationStageMask: GpuStage.ComputeShader, sourceAccessMask: GpuAccess.ShaderRead, destinationAccessMask: GpuAccess.ShaderWrite);
        region.RecordCopy(1, 0);
        Assert.Equal(7, gpu.DeviceLocal(Page)[0]);
    }

    private static void Copy(UploadModelGpu gpu, GpuRegion region) {
        var recording = new GpuRegionCopyRecording(gpu.Services.Recorder, GpuStage.ComputeShader, static () => 2);
        recording.Record(region, 0, handsToReaders: true);
        recording.Finish();
    }
}
