using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuStorageBufferFactory"/> by forwarding to <see cref="IVulkanStorageBufferFactory"/>,
/// resolving the Vulkan instance and logical device from the device context.
/// </summary>
public sealed class VulkanGpuStorageBufferFactory(IVulkanStorageBufferFactory storageBufferFactory) : IGpuStorageBufferFactory {
    /// <inheritdoc/>
    public IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        return storageBufferFactory.Create(
            logicalDevice: vkContext.LogicalDevice,
            sizeBytes: sizeBytes,
            vulkanInstance: vkContext.Instance
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        // Back it with device-local (not host-visible) memory — a GPU-only storage buffer (UAV target) that is never
        // host-mapped, matching the Direct3D 12 default-heap UAV buffer.
        return storageBufferFactory.Create(
            deviceLocal: true,
            logicalDevice: vkContext.LogicalDevice,
            sizeBytes: sizeBytes,
            vulkanInstance: vkContext.Instance
        );
    }
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes, bool deviceLocal = false) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        // Indirect-capable. Host-visible by default (the CPU fills it via Write before submit — host-coherent, no extra
        // barrier); device-local when a compute shader writes it as a UAV (GPU-computed args), ordered by a barrier.
        return storageBufferFactory.Create(
            deviceLocal: deviceLocal,
            indirectArgs: true,
            logicalDevice: vkContext.LogicalDevice,
            sizeBytes: sizeBytes,
            vulkanInstance: vkContext.Instance
        );
    }
}
