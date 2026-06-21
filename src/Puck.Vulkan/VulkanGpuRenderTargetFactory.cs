using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuRenderTargetFactory"/> by creating <see cref="VulkanViewTarget"/> instances,
/// converting <see cref="GpuPixelFormat"/> constants to <c>VkFormat</c> values.
/// </summary>
public sealed class VulkanGpuRenderTargetFactory(
    IVulkanOffscreenImageApi offscreenImageApi,
    IVulkanRenderPassApi renderPassApi,
    IVulkanFramebufferSetApi framebufferSetApi,
    IVulkanCommandResourcesFactory commandResourcesFactory
) : IGpuRenderTargetFactory {
    /// <inheritdoc/>
    public IGpuRenderTarget Create(IGpuDeviceContext deviceContext, uint format, uint width, uint height) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        return new VulkanViewTarget(
            commandResourcesFactory: commandResourcesFactory,
            format: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
            framebufferSetApi: framebufferSetApi,
            height: height,
            instance: vkContext.Instance,
            logicalDevice: vkContext.LogicalDevice,
            offscreenImageApi: offscreenImageApi,
            renderPassApi: renderPassApi,
            width: width
        );
    }
}
