using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for the Direct3D 12 submission batch on a software (WARP) device: a batch defers its lists, and any
/// operation put on the queue through the context's queue handle (a release's drain, a raw upload, a signal) follows
/// every list the batch deferred before it, while the batch stays open for the lists after it; a wait on a deferred
/// fence keeps it open too.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSubmissionBatchLawTests {
    [Fact]
    public void EmptyFencedAndBlockingSubmissionsCoverEarlierWorkAndKeepTheBatchOpen() {
        using var context = DirectXTestDevices.Warp();
        var gpu = context.Services;
        using var commands = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "empty boundary"));
        using var earlier = gpu.QueueSubmitter.CreateSubmissionFence();
        using var empty = gpu.QueueSubmitter.CreateSubmissionFence();
        Record(gpu, commands);
        gpu.QueueSubmitter.BeginBatch();
        try {
            gpu.QueueSubmitter.Submit([commands.CommandBufferHandle], earlier);
            gpu.QueueSubmitter.Submit([], empty);
            empty.Wait();
            Assert.True(earlier.IsSignaled);
            earlier.Wait();
            gpu.QueueSubmitter.Submit([commands.CommandBufferHandle], earlier);
            Assert.False(earlier.IsSignaled);
            gpu.QueueSubmitter.SubmitAndWait([]);
            Assert.True(earlier.IsSignaled);
            earlier.Wait();
            gpu.QueueSubmitter.Submit([commands.CommandBufferHandle], earlier);
            Assert.False(earlier.IsSignaled);
        } finally {
            gpu.QueueSubmitter.FlushBatch();
        }
        earlier.Wait();
    }

    [Fact]
    public void BackgroundWorkBetweenFrameListsSharesTheirOrderAndOwnerLifetime() {
        using var context = DirectXTestDevices.Warp();
        var gpu = context.Services;
        using var first = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "frame first"));
        using var work = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "background"));
        using var last = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "frame last"));
        using var before = gpu.QueueSubmitter.CreateSubmissionFence();
        using var background = gpu.QueueSubmitter.CreateSubmissionFence();
        using var owner = gpu.QueueSubmitter.CreateSubmissionFence();
        Record(gpu, first);
        Record(gpu, work);
        Record(gpu, last);
        gpu.QueueSubmitter.BeginBatch();
        try {
            gpu.QueueSubmitter.Submit([first.CommandBufferHandle], before);
            gpu.QueueSubmitter.Submit([work.CommandBufferHandle], background);
            background.Wait();
            Assert.True(before.IsSignaled);
            gpu.QueueSubmitter.Submit([last.CommandBufferHandle], owner);
            Assert.False(owner.IsSignaled);
        } finally {
            gpu.QueueSubmitter.FlushBatch();
        }
        owner.Dispose();
        before.Wait();
    }

    [Fact]
    public void AReleaseThatDrainsTheQueueInsideABatchCoversTheListsDeferredBeforeIt() {
        using var context = DirectXTestDevices.Warp();
        var gpu = context.Services;
        using var first = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "first"));
        using var second = gpu.CommandPoolFactory.Create(name: new GpuObjectName(owner: "batch law", part: "second"));
        using var deferred = gpu.QueueSubmitter.CreateSubmissionFence();
        using var later = gpu.QueueSubmitter.CreateSubmissionFence();
        var image = new DirectXGpuSurfaceExportFactory(deviceContext: context).CreateSimultaneousAccessImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 4U,
            width: 4U
        );

        Record(gpu: gpu, pool: first);
        Record(gpu: gpu, pool: second);
        gpu.QueueSubmitter.BeginBatch();
        try {
            gpu.QueueSubmitter.Submit(commandBufferHandles: [first.CommandBufferHandle], fence: deferred);
            Assert.False(condition: deferred.IsSignaled);

            // The release drains every submission made so far before it frees the texture a deferred list may read.
            image.Dispose();
            Assert.True(condition: deferred.IsSignaled);

            // The batch is still open: a submission after the release is deferred again.
            gpu.QueueSubmitter.Submit(commandBufferHandles: [second.CommandBufferHandle], fence: later);
            Assert.False(condition: later.IsSignaled);

            // A wait on a deferred fence hands the batch to the queue and keeps it open.
            later.Wait();
            deferred.Wait();
            gpu.QueueSubmitter.Submit(commandBufferHandles: [first.CommandBufferHandle], fence: deferred);
            Assert.False(condition: deferred.IsSignaled);
        } finally {
            gpu.QueueSubmitter.FlushBatch();
        }

        later.Wait();
        deferred.Wait();
    }

    private static void Record(GpuDeviceServices gpu, IGpuCommandPool pool) {
        gpu.Recorder.BeginCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
        gpu.Recorder.EndCommandBuffer(commandBufferHandle: pool.CommandBufferHandle);
    }
}
