namespace Puck.Abstractions.Gpu;

/// <summary>
/// Where a buffer the host writes and the GPU reads directly lives: a <see cref="GpuRegion"/>'s ring or in-place buffer,
/// as <see cref="GpuResidency.RingMemory"/> chooses from the device's memory profile.
/// </summary>
public enum GpuHostVisibleMemory {
    /// <summary>Host memory the GPU reads over the bus, or on unified memory the one pool
    /// (<see cref="IGpuBufferFactory.CreateHostVisible(ulong, GpuBufferUsage)"/>).</summary>
    Host = 0,
    /// <summary>The device-local aperture a discrete adapter exposes to host writes
    /// (<see cref="IGpuBufferFactory.CreateHostVisibleDeviceLocal"/>).</summary>
    DeviceLocal = 1,
}
