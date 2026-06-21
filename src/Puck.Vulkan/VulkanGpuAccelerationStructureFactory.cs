using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructureFactory"/> for Vulkan by wrapping the world-acceleration builder
/// and the resolved Vulkan device context in a <see cref="VulkanGpuAccelerationStructure"/>.
/// </summary>
public sealed class VulkanGpuAccelerationStructureFactory(IVulkanWorldAccelerationApi worldAccelerationApi) : IGpuAccelerationStructureFactory {
    /// <inheritdoc/>
    public IGpuAccelerationStructure Create(IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        return new VulkanGpuAccelerationStructure(
            api: worldAccelerationApi,
            deviceContext: (IVulkanDeviceContext)deviceContext
        );
    }
}
