using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> by creating adapter wrappers over
/// <see cref="VulkanSurfaceReadback"/>, <see cref="VulkanSurfaceUpload"/>, and <see cref="VulkanSurfaceImport"/>.
/// Each wrapper downcasts <see cref="IGpuDeviceContext"/> to <see cref="IVulkanDeviceContext"/> and converts
/// <see cref="GpuPixelFormat"/> constants to <c>VkFormat</c> values at call time.
/// </summary>
public sealed class VulkanGpuSurfaceTransferFactory(
    IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
    IVulkanCommandResourcesFactory commandResourcesFactory,
    IVulkanExternalMemoryApi externalMemoryApi,
    IVulkanFramebufferSetApi framebufferSetApi,
    IVulkanFrameReadbackApi frameReadbackApi,
    IVulkanFrameSynchronizationApi frameSynchronizationApi,
    IVulkanOffscreenImageApi offscreenImageApi,
    IVulkanStorageBufferFactory storageBufferFactory,
    VulkanQueueSubmitter queueSubmitter
) : IGpuSurfaceTransferFactory {
    /// <inheritdoc/>
    public IGpuSurfaceReadback CreateReadback(IGpuDeviceContext deviceContext) =>
        new VulkanGpuSurfaceReadback(inner: new VulkanSurfaceReadback(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            frameReadbackApi: frameReadbackApi,
            frameSynchronizationApi: frameSynchronizationApi,
            queueSubmitter: queueSubmitter
        ));
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload(IGpuDeviceContext deviceContext) =>
        // The frame-synchronization API opts the upload into its PIPELINED mode (fenced fire-and-forget — see
        // VulkanSurfaceUpload's remarks), so a per-frame feed behind the frame-ring host never drains the queue.
        new VulkanGpuSurfaceUpload(inner: new VulkanSurfaceUpload(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            framebufferSetApi: framebufferSetApi,
            frameSynchronizationApi: frameSynchronizationApi,
            offscreenImageApi: offscreenImageApi,
            queueSubmitter: queueSubmitter,
            storageBufferFactory: storageBufferFactory
        ));
    /// <inheritdoc/>
    public IGpuSurfaceImport CreateImport(IGpuDeviceContext deviceContext) =>
        new VulkanGpuSurfaceImport(inner: new VulkanSurfaceImport(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            externalMemoryApi: externalMemoryApi,
            framebufferSetApi: framebufferSetApi,
            queueSubmitter: queueSubmitter
        ));
}

file sealed class VulkanGpuSurfaceReadback(VulkanSurfaceReadback inner) : IGpuSurfaceReadback {
    public ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel) =>
        inner.Read(
            bytesPerPixel: bytesPerPixel,
            deviceContext: (IVulkanDeviceContext)deviceContext,
            height: height,
            sourceImageHandle: sourceImageHandle,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
    public void SubmitRead(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel) =>
        inner.SubmitRead(
            bytesPerPixel: bytesPerPixel,
            deviceContext: (IVulkanDeviceContext)deviceContext,
            height: height,
            sourceImageHandle: sourceImageHandle,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
    public bool IsReadComplete() => inner.IsReadComplete();
    public ReadOnlyMemory<byte> MapPixels() => inner.MapPixels();
    public void Dispose() => inner.Dispose();
}
file sealed class VulkanGpuSurfaceUpload(VulkanSurfaceUpload inner) : IGpuSurfaceUpload {
    public nint Upload(IGpuDeviceContext deviceContext, ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height) =>
        inner.Upload(
            deviceContext: (IVulkanDeviceContext)deviceContext,
            height: height,
            pixels: pixels,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
    public void Dispose() => inner.Dispose();
}
file sealed class VulkanGpuSurfaceImport(VulkanSurfaceImport inner) : IGpuSurfaceImport {
    public nint Import(IGpuDeviceContext deviceContext, nint sharedHandle, GpuPixelFormat format, uint width, uint height) =>
        inner.Import(
            deviceContext: (IVulkanDeviceContext)deviceContext,
            height: height,
            sharedHandle: sharedHandle,
            vulkanFormat: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            width: width
        );
    public void Dispose() => inner.Dispose();
}
