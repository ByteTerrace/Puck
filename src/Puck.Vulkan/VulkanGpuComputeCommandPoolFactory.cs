using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuComputeCommandPoolFactory"/> for Vulkan by allocating a single-command-buffer
/// <c>VulkanCommandResources</c> through <see cref="IVulkanCommandResourcesFactory"/>.
/// </summary>
public sealed class VulkanGpuComputeCommandPoolFactory(IVulkanCommandResourcesFactory commandResourcesFactory) : IGpuComputeCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuComputeCommandPool Create(IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var logicalDevice = ((IVulkanDeviceContext)deviceContext).LogicalDevice;

        return new VulkanGpuComputeCommandPool(commandResources: commandResourcesFactory.Create(commandBufferCount: 1, logicalDevice: logicalDevice));
    }
}
