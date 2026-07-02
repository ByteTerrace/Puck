using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuStorageImageFactory"/> for Vulkan: it creates a STORAGE + SAMPLED + TRANSFER_SRC color
/// image (so a compute shader writes it, a compositor samples it, and a readback copies it) plus a 2D view,
/// converting the <see cref="GpuPixelFormat"/> to a <c>VkFormat</c>.
/// </summary>
public sealed class VulkanGpuStorageImageFactory(IVulkanOffscreenImageApi offscreenImageApi, IVulkanFramebufferSetApi framebufferSetApi) : IGpuStorageImageFactory {
    /// <inheritdoc/>
    public IGpuStorageImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var vkContext = (IVulkanDeviceContext)deviceContext;
        var logicalDevice = vkContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;
        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
        var image = offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
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

        return new VulkanGpuStorageImage(
            deviceHandle: deviceHandle,
            framebufferSetApi: framebufferSetApi,
            height: height,
            imageHandle: image.ImageHandle,
            imageViewHandle: imageViewHandle,
            memoryHandle: image.MemoryHandle,
            offscreenImageApi: offscreenImageApi,
            width: width
        );
    }
}
