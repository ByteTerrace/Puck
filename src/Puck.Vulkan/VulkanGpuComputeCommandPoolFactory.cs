using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuComputeCommandPoolFactory"/> for Vulkan by allocating a single-command-buffer
/// <c>VulkanCommandResources</c> through <see cref="IVulkanCommandResourcesFactory"/>.
/// </summary>
public sealed class VulkanGpuComputeCommandPoolFactory(IVulkanDeviceContext deviceContext, IVulkanCommandResourcesFactory commandResourcesFactory) : IGpuComputeCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuComputeCommandPool Create() {

        var logicalDevice = deviceContext.LogicalDevice;

        return new VulkanGpuComputeCommandPool(commandResources: commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: logicalDevice
        ));
    }
}
