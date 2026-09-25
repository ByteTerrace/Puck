using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuCommandPoolFactory"/> for Vulkan by allocating a single-command-buffer
/// <c>VulkanCommandResources</c> through <see cref="IVulkanCommandResourcesFactory"/>.
/// </summary>
public sealed class VulkanGpuCommandPoolFactory(IVulkanDeviceContext deviceContext, IVulkanCommandResourcesFactory commandResourcesFactory) : IGpuCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuCommandPool Create() {

        var logicalDevice = deviceContext.LogicalDevice;

        return new VulkanGpuCommandPool(commandResources: commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: logicalDevice
        ));
    }
}
