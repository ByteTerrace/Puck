using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuStorageBufferFactory"/> over <see cref="IVulkanBufferApi"/>: every storage buffer carries
/// <see cref="VulkanBufferUsageFlags.Storage"/>, an indirect-argument buffer adds
/// <see cref="VulkanBufferUsageFlags.IndirectBuffer"/>, and a host-written buffer is
/// <see cref="VulkanBufferMemory.HostCoherent"/> while a device-written one is <see cref="VulkanBufferMemory.DeviceLocal"/>.
/// </summary>
public sealed class VulkanGpuStorageBufferFactory(IVulkanBufferApi bufferApi) : IGpuStorageBufferFactory {
    /// <inheritdoc/>
    public IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: ((IVulkanDeviceContext)deviceContext),
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: sizeBytes,
            usage: VulkanBufferUsageFlags.Storage
        );
    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        // Device-local, never host-mapped: a GPU-only storage buffer, matching the Direct3D 12 default-heap UAV buffer.
        VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: ((IVulkanDeviceContext)deviceContext),
            memory: VulkanBufferMemory.DeviceLocal,
            sizeBytes: sizeBytes,
            usage: VulkanBufferUsageFlags.Storage
        );
    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: ((IVulkanDeviceContext)deviceContext),
            memory: VulkanBufferMemory.DeviceLocal,
            sizeBytes: sizeBytes,
            usage: VulkanBufferUsageFlags.Storage | VulkanBufferUsageFlags.IndirectBuffer
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        // The CPU fills it through Write before submit; host-coherent, so no barrier is needed for that write.
        VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: ((IVulkanDeviceContext)deviceContext),
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: sizeBytes,
            usage: VulkanBufferUsageFlags.Storage | VulkanBufferUsageFlags.IndirectBuffer
        );
}
