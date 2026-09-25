using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuCommandPoolFactory"/> for Vulkan by allocating a single-command-buffer
/// <c>VulkanCommandResources</c> through <see cref="IVulkanCommandResourcesFactory"/>.
/// </summary>
/// <param name="deviceContext">The device context every pool is created on.</param>
/// <param name="commandResourcesFactory">The factory the pool and its command buffer come from.</param>
/// <param name="naming">The naming every created object is handed to.</param>
public sealed class VulkanGpuCommandPoolFactory(IVulkanDeviceContext deviceContext, IVulkanCommandResourcesFactory commandResourcesFactory, GpuObjectNaming naming) : IGpuCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuCommandPool Create(in GpuObjectName name) {
        var logicalDevice = deviceContext.LogicalDevice;
        var resources = commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: logicalDevice
        );

        naming.Name(
            handle: resources.CommandPoolHandle,
            kind: GpuObjectKind.CommandPool,
            name: in name
        );
        naming.Name(
            handle: resources.CommandBufferHandles[0],
            kind: GpuObjectKind.CommandBuffer,
            name: in name
        );

        return new VulkanGpuCommandPool(commandResources: resources);
    }
}
