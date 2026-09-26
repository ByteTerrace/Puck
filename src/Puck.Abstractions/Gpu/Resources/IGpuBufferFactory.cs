namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates buffers on the device its backend implementation is bound to, by usage and placement. A host-visible buffer
/// is host-writable, so it comes back as an <see cref="IGpuStorageBuffer"/>; a device-local buffer only the GPU writes,
/// so it comes back as an <see cref="IGpuBuffer"/>, which has no host-write operation.
/// <para>Placement on each backend: a host-visible buffer is a Vulkan <c>HOST_COHERENT</c> allocation or a Direct3D 12
/// upload-heap buffer, permanently mapped and permanently in <c>GENERIC_READ</c>, which already covers the vertex,
/// index, constant and indirect-argument states. A device-local buffer is a Vulkan <c>DEVICE_LOCAL</c> allocation or a
/// Direct3D 12 default-heap buffer created in <c>COMMON</c>; with <see cref="GpuBufferUsage.Storage"/> it allows
/// unordered access, and an indirect dispatch reading one a shader wrote transitions it into
/// <c>INDIRECT_ARGUMENT</c> first. A host-visible device-local buffer is a Vulkan <c>DEVICE_LOCAL</c>,
/// <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c> allocation or a Direct3D 12 <c>GPU_UPLOAD</c>-heap buffer, mapped and
/// read-only to shaders like a host-visible one, but in the adapter's memory.</para>
/// </summary>
public interface IGpuBufferFactory {
    /// <summary>Creates a device-local buffer.</summary>
    /// <param name="sizeBytes">The size, in bytes, of the buffer; not zero.</param>
    /// <param name="usage">The usages; <see cref="GpuBufferUsages.Validate"/> states the rules.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuBuffer"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name);
    /// <summary>Creates a host-visible buffer.</summary>
    /// <param name="sizeBytes">The size, in bytes, of the buffer; not zero.</param>
    /// <param name="usage">The usages; <see cref="GpuBufferUsages.Validate"/> states the rules.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuStorageBuffer"/>, its contents undefined until written.</returns>
    /// <exception cref="ArgumentException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name);
    /// <summary>Creates a host-visible buffer in the device-local aperture: host writes reach the adapter's memory
    /// through a coherent mapping, and the GPU reads it at device-local speed. Only a device whose memory profile reports
    /// host-visible device-local memory on a discrete adapter (<see cref="GpuResidency.RingMemory"/>) has one; elsewhere
    /// the creation fails.</summary>
    /// <param name="sizeBytes">The size, in bytes, of the buffer; not zero.</param>
    /// <param name="usage">The usages; <see cref="GpuBufferUsages.Validate"/> states the rules.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuStorageBuffer"/>, its contents undefined until written.</returns>
    /// <exception cref="ArgumentException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    IGpuStorageBuffer CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name);
    /// <summary>Creates a host-visible buffer holding a copy of the supplied bytes.</summary>
    /// <param name="data">The bytes to copy in; not empty.</param>
    /// <param name="usage">The usages; <see cref="GpuBufferUsages.Validate"/> states the rules.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuStorageBuffer"/> the size of <paramref name="data"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is empty or undefined.</exception>
    IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name);
}
