namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral storage buffers.
/// </summary>
public interface IGpuStorageBufferFactory {
    /// <summary>Creates a host-visible storage buffer of the given size.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <returns>A new, owning <see cref="IGpuStorageBuffer"/>.</returns>
    IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes);
    /// <summary>Creates a device-local storage buffer the GPU writes (a Vulkan storage buffer, or a Direct3D 12
    /// default-heap buffer that allows unordered access). Unlike <see cref="Create"/> it is not host-writable; bind
    /// it read-write.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <returns>A new, owning <see cref="IGpuBuffer"/> that exposes no host-write operation.</returns>
    IGpuBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes);
    /// <summary>Creates a host-writable buffer (fill it with group counts through <see cref="IGpuStorageBuffer"/>)
    /// that is also a legal indirect-dispatch argument source for
    /// <see cref="IGpuComputeRecorder.DispatchIndirect"/>. On Vulkan it is a host-visible storage buffer carrying
    /// <c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c>; on Direct3D 12 it is an upload-heap buffer (its <c>GENERIC_READ</c>
    /// state already permits indirect-argument reads, so no buffer-state transition is needed).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer (at least 12 — one <c>uint3</c> group count).</param>
    /// <returns>A new, owning <see cref="IGpuStorageBuffer"/>.</returns>
    IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes);
    /// <summary>Creates a device-local indirect-argument buffer written by the GPU. It is deliberately returned as
    /// <see cref="IGpuBuffer"/>, which has no host-write operation.</summary>
    IGpuBuffer CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes);
}
