using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuVertexBufferFactory"/> by forwarding to <see cref="IVulkanVertexBufferFactory"/>,
/// resolving the Vulkan instance and logical device from the device context.
/// </summary>
public sealed class VulkanGpuVertexBufferFactory(IVulkanVertexBufferFactory vertexBufferFactory) : IGpuVertexBufferFactory {
    /// <inheritdoc/>
    public IGpuVertexBuffer Create(IGpuDeviceContext deviceContext, byte[] vertexData, uint strideBytes) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        return vertexBufferFactory.Create(
            logicalDevice: vkContext.LogicalDevice,
            vertexData: vertexData,
            vulkanInstance: vkContext.Instance
        );
    }
}
