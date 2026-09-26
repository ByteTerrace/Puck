using System.Diagnostics.CodeAnalysis;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> by creating adapter wrappers over
/// <see cref="VulkanSurfaceReadback"/>, <see cref="VulkanSurfaceUpload"/>, and <see cref="VulkanSurfaceImport"/>, each
/// bound to the factory's device context. Each wrapper converts <see cref="GpuPixelFormat"/> constants to
/// <c>VkFormat</c> values at call time.
/// </summary>
/// <param name="deviceContext">The device context every object the factory creates works on.</param>
/// <param name="bufferApi">The API that makes staging and readback buffers.</param>
/// <param name="commandBufferRecordingApi">The API used to record copies and transitions.</param>
/// <param name="commandResourcesFactory">The factory for copy command resources.</param>
/// <param name="externalMemoryApi">The API that imports shared allocations.</param>
/// <param name="framebufferSetApi">The API that creates and destroys image views.</param>
/// <param name="frameSynchronizationApi">The API for the upload's pipelined fence.</param>
/// <param name="offscreenImageApi">The API that creates the upload's sampled image.</param>
/// <param name="queueSubmitter">The queue submission service.</param>
public sealed class VulkanGpuSurfaceTransferFactory(
    IVulkanDeviceContext deviceContext,
    IVulkanBufferApi bufferApi,
    IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
    IVulkanCommandResourcesFactory commandResourcesFactory,
    IVulkanExternalMemoryApi externalMemoryApi,
    IVulkanFramebufferSetApi framebufferSetApi,
    IVulkanFrameSynchronizationApi frameSynchronizationApi,
    IVulkanOffscreenImageApi offscreenImageApi,
    VulkanQueueSubmitter queueSubmitter
) : IGpuSurfaceTransferFactory {
    /// <inheritdoc/>
    /// <remarks>Imports the handle into a timeline semaphore as a <see cref="VulkanSharedFence"/>.</remarks>
    public bool TryImportFence(nint sharedHandle, [NotNullWhen(true)] out IGpuSharedFence? fence, out string refusal) {
        var imported = VulkanSharedFence.TryImport(
            device: deviceContext.LogicalDevice.Commands,
            fence: out var shared,
            refusal: out refusal,
            sharedHandle: sharedHandle
        );

        fence = shared;

        return imported;
    }
    /// <inheritdoc/>
    public IGpuSurfaceImport CreateImport() =>
        new VulkanGpuSurfaceImport(inner: new VulkanSurfaceImport(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            deviceContext: deviceContext,
            externalMemoryApi: externalMemoryApi,
            framebufferSetApi: framebufferSetApi,
            queueSubmitter: queueSubmitter
        ));
    /// <inheritdoc/>
    public IGpuSurfaceReadback CreateReadback() =>
        new VulkanGpuSurfaceReadback(inner: new VulkanSurfaceReadback(
            bufferApi: bufferApi,
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            deviceContext: deviceContext,
            queueSubmitter: queueSubmitter
        ));
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload() =>
        // The frame-synchronization API opts the upload into its PIPELINED mode (fenced fire-and-forget — see
        // VulkanSurfaceUpload's remarks), so a per-frame feed behind the frame-ring host never drains the queue.
        new VulkanGpuSurfaceUpload(inner: new VulkanSurfaceUpload(
            bufferApi: bufferApi,
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            deviceContext: deviceContext,
            frameSynchronizationApi: frameSynchronizationApi,
            framebufferSetApi: framebufferSetApi,
            offscreenImageApi: offscreenImageApi,
            queueSubmitter: queueSubmitter
        ));
}

file sealed class VulkanGpuSurfaceReadback(VulkanSurfaceReadback inner) : IGpuSurfaceReadback {
    public void Dispose() => inner.Dispose();
    public ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) =>
        inner.Read(
            bytesPerPixel: bytesPerPixel,
            height: height,
            sourceImageHandle: sourceImageHandle,
            sourceLayout: sourceLayout,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
}
file sealed class VulkanGpuSurfaceUpload(VulkanSurfaceUpload inner) : IGpuSurfaceUpload {
    public void Dispose() => inner.Dispose();
    public nint Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height) =>
        inner.Upload(
            height: height,
            pixels: pixels,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
}
file sealed class VulkanGpuSurfaceImport(VulkanSurfaceImport inner) : IGpuSurfaceImport {
    public void Dispose() => inner.Dispose();
    public GpuImportedSurface Import(nint sharedHandle, GpuPixelFormat format, uint width, uint height) {
        var imageViewHandle = inner.Import(
            height: height,
            sharedHandle: sharedHandle,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );

        return new GpuImportedSurface(
            ImageHandle: inner.ImageHandle,
            ImageViewHandle: imageViewHandle
        );
    }
}
