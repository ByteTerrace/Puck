using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuSurfaceExportFactory"/> for Vulkan by creating
/// <see cref="VulkanGpuExportableRenderTarget"/> and <see cref="VulkanGpuExportableStorageImage"/> instances,
/// converting <see cref="GpuPixelFormat"/> constants to <c>VkFormat</c> values.
/// </summary>
public sealed class VulkanGpuSurfaceExportFactory(
    IVulkanExternalMemoryApi externalMemoryApi,
    IVulkanRenderPassApi renderPassApi,
    IVulkanFramebufferSetApi framebufferSetApi,
    IVulkanCommandResourcesFactory commandResourcesFactory,
    IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
    VulkanQueueSubmitter queueSubmitter
) : IGpuSurfaceExportFactory {
    /// <inheritdoc/>
    public IGpuExportableRenderTarget CreateExportableTarget(IGpuDeviceContext deviceContext, uint width, uint height, uint format) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        return new VulkanGpuExportableRenderTarget(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            externalMemoryApi: externalMemoryApi,
            format: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            framebufferSetApi: framebufferSetApi,
            height: height,
            instance: vkContext.Instance,
            logicalDevice: vkContext.LogicalDevice,
            queueSubmitter: queueSubmitter,
            renderPassApi: renderPassApi,
            width: width
        );
    }

    /// <inheritdoc/>
    public IGpuExportableStorageImage CreateExportableStorageImage(IGpuDeviceContext deviceContext, uint width, uint height, uint format) {
        var vkContext = (IVulkanDeviceContext)deviceContext;
        var logicalDevice = vkContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;
        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
        var image = externalMemoryApi.CreateExportableImage(request: new VulkanExternalImageExportRequest(
            DeviceHandle: deviceHandle,
            Format: vkFormat,
            Height: height,
            InstanceHandle: vkContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.Storage | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: width
        ));

        framebufferSetApi.CreateImageView(
            imageViewHandle: out var imageViewHandle,
            request: new VulkanImageViewCreateRequest(DeviceHandle: deviceHandle, Format: vkFormat, ImageHandle: image.ImageHandle)
        ).ThrowIfFailed(operation: "vkCreateImageView");

        return new VulkanGpuExportableStorageImage(
            externalMemoryApi: externalMemoryApi,
            framebufferSetApi: framebufferSetApi,
            height: height,
            imageHandle: image.ImageHandle,
            imageViewHandle: imageViewHandle,
            logicalDevice: logicalDevice,
            memoryHandle: image.MemoryHandle,
            sharedHandle: image.SharedHandle,
            width: width
        );
    }
}
