namespace Puck.Abstractions.Gpu;

/// <summary>
/// What an allocation is for, which decides whether <see cref="GpuDeviceMemoryWork"/> counts it. The role is the
/// caller's placement, never the memory type the driver chose: on a unified-memory device every Vulkan memory type can
/// carry <c>DEVICE_LOCAL</c>, and a host-visible buffer there is still host memory by role.
/// </summary>
public enum GpuMemoryRole {
    /// <summary>Memory the device reads and writes and the host never maps: images, device-local buffers (a Vulkan
    /// device-local buffer, a Direct3D 12 <c>DEFAULT</c>-heap buffer), exportable images, and imported memory.</summary>
    DeviceLocal,
    /// <summary>Memory the host maps: host-visible buffers, staging buffers, and Direct3D 12 <c>UPLOAD</c> and
    /// <c>READBACK</c> heaps.</summary>
    HostVisible,
    /// <summary>Device memory the host maps through a discrete adapter's aperture: a host-visible device-local buffer
    /// (a Vulkan <c>DEVICE_LOCAL</c>, <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c> allocation, a Direct3D 12
    /// <c>GPU_UPLOAD</c>-heap buffer). It consumes the adapter's memory, so it counts like
    /// <see cref="DeviceLocal"/>.</summary>
    HostVisibleDeviceLocal,
}
