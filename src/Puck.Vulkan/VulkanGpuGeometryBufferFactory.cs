using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuGeometryBufferFactory"/> over <see cref="IVulkanBufferApi"/>: a buffer in
/// <see cref="VulkanBufferMemory.HostCoherent"/> memory whose usage is <see cref="VulkanBufferUsageFlags.VertexBuffer"/>,
/// <see cref="VulkanBufferUsageFlags.IndexBuffer"/>, or both, sized to the data and filled with it.
/// </summary>
public sealed class VulkanGpuGeometryBufferFactory(IVulkanBufferApi bufferApi) : IGpuGeometryBufferFactory {
    /// <summary>Converts declared geometry usages to <c>VkBufferUsageFlags</c>.</summary>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The Vulkan usage flags.</returns>
    public static uint ToVkBufferUsage(GpuBufferUsage usage) =>
        (((usage & GpuBufferUsage.Vertex) != 0)
            ? VulkanBufferUsageFlags.VertexBuffer
            : 0U) | (((usage & GpuBufferUsage.Index) != 0)
            ? VulkanBufferUsageFlags.IndexBuffer
            : 0U);
    /// <inheritdoc/>
    public IGpuBuffer Create(IGpuDeviceContext deviceContext, ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        GpuBufferUsages.Validate(
            sizeBytes: data.Length,
            usage: usage
        );

        var buffer = VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: ((IVulkanDeviceContext)deviceContext),
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: ((ulong)data.Length),
            usage: ToVkBufferUsage(usage: usage)
        );

        try {
            buffer.Write<byte>(data: data);
        } catch {
            buffer.Dispose();

            throw;
        }

        return buffer;
    }
}
