using System.Diagnostics.CodeAnalysis;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuSurfaceTransferFactory"/> for Vulkan: the neutral <see cref="GpuSurfaceReadback"/> and
/// <see cref="GpuSurfaceUpload"/> over the device context's services, and an adapter over
/// <see cref="VulkanSurfaceImport"/> that converts <see cref="GpuPixelFormat"/> constants to <c>VkFormat</c> values at
/// call time, each bound to the factory's device context.
/// </summary>
/// <param name="deviceContext">The device context every object the factory creates works on; also its neutral
/// <see cref="IGpuDeviceContext"/>.</param>
/// <param name="commandBufferRecordingApi">The API used to record an import's transitions.</param>
/// <param name="commandResourcesFactory">The factory for an import's command resources.</param>
/// <param name="externalMemoryApi">The API that imports shared allocations.</param>
/// <param name="framebufferSetApi">The API that creates and destroys image views.</param>
/// <param name="queueSubmitter">The queue submission service.</param>
public sealed class VulkanGpuSurfaceTransferFactory(
    IVulkanDeviceContext deviceContext,
    IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
    IVulkanCommandResourcesFactory commandResourcesFactory,
    IVulkanExternalMemoryApi externalMemoryApi,
    IVulkanFramebufferSetApi framebufferSetApi,
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
    /// <remarks>Imports the texture and the fence as a <see cref="VulkanImportedWritableImage"/>.</remarks>
    public bool TryImportWritable(nint sharedHandle, nint sharedFenceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, [NotNullWhen(true)] out IGpuExportableImage? image, out string refusal) {
        var imported = VulkanImportedWritableImage.TryImport(
            commandResourcesFactory: commandResourcesFactory,
            deviceContext: deviceContext,
            externalMemoryApi: externalMemoryApi,
            format: format,
            framebufferSetApi: framebufferSetApi,
            height: height,
            image: out var writable,
            queueSubmitter: queueSubmitter,
            recording: commandBufferRecordingApi,
            refusal: out refusal,
            sharedFenceHandle: sharedFenceHandle,
            sharedHandle: sharedHandle,
            usage: usage,
            width: width
        );

        image = writable;

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
        new GpuSurfaceReadback(
            device: () => deviceContext.LogicalDevice,
            deviceContext: Neutral
        );
    /// <inheritdoc/>
    public IGpuSurfaceUpload CreateUpload() =>
        new GpuSurfaceUpload(
            device: () => deviceContext.LogicalDevice,
            deviceContext: Neutral
        );

    private IGpuDeviceContext Neutral =>
        ((deviceContext as IGpuDeviceContext) ?? throw new InvalidOperationException(message: $"The Vulkan device context {deviceContext.GetType().Name} is not a neutral device context, which a surface upload or readback records through."));
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
