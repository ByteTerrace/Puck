using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuBufferFactory"/> for Vulkan through <see cref="IVulkanBufferApi"/>, on its device context:
/// a host-visible buffer is a host-coherent allocation mapped for its lifetime, a host-visible device-local one a
/// <c>DEVICE_LOCAL</c>, host-coherent allocation (the aperture) mapped the same way, and a device-local one a
/// <c>DEVICE_LOCAL</c> allocation that is never mapped.
/// </summary>
/// <param name="deviceContext">The device context every buffer is created on.</param>
/// <param name="bufferApi">The native buffer API.</param>
public sealed class VulkanGpuBufferFactory(IVulkanDeviceContext deviceContext, IVulkanBufferApi bufferApi) : IGpuBufferFactory {
    /// <summary>Returns the <c>VkBufferUsageFlags</c> a buffer of the given neutral usages is created with. A storage
    /// buffer is also a transfer source and destination (<see cref="VulkanBufferUsageFlags.Storage"/>).</summary>
    /// <param name="usage">The neutral usages.</param>
    /// <returns>The Vulkan usage flags.</returns>
    public static uint ToVkBufferUsage(GpuBufferUsage usage) {
        var result = 0U;

        if (0 != (usage & GpuBufferUsage.Vertex)) {
            result |= VulkanBufferUsageFlags.VertexBuffer;
        }

        if (0 != (usage & GpuBufferUsage.Index)) {
            result |= VulkanBufferUsageFlags.IndexBuffer;
        }

        if (0 != (usage & GpuBufferUsage.Storage)) {
            result |= VulkanBufferUsageFlags.Storage;
        }

        if (0 != (usage & GpuBufferUsage.Uniform)) {
            result |= VulkanBufferUsageFlags.UniformBuffer;
        }

        if (0 != (usage & GpuBufferUsage.Indirect)) {
            result |= VulkanBufferUsageFlags.IndirectBuffer;
        }

        return result;
    }
    /// <inheritdoc/>
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) =>
        Create(
            memory: VulkanBufferMemory.DeviceLocal,
            sizeBytes: sizeBytes,
            usage: usage
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) =>
        Create(
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: sizeBytes,
            usage: usage
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) =>
        Create(
            memory: VulkanBufferMemory.HostCoherentDeviceLocal,
            sizeBytes: sizeBytes,
            usage: usage
        );
    /// <inheritdoc/>
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        var buffer = Create(
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: ((ulong)data.Length),
            usage: usage
        );

        try {
            buffer.Write<byte>(data: data);
        } catch {
            buffer.Dispose();

            throw;
        }

        return buffer;
    }

    private VulkanBuffer Create(VulkanBufferMemory memory, ulong sizeBytes, GpuBufferUsage usage) {
        GpuBufferUsages.Validate(
            sizeBytes: sizeBytes,
            usage: usage
        );

        return VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: deviceContext,
            memory: memory,
            sizeBytes: sizeBytes,
            usage: ToVkBufferUsage(usage: usage)
        );
    }
}
