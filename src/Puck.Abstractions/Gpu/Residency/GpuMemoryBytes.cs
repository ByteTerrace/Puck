namespace Puck.Abstractions.Gpu;

/// <summary>A count of buffer bytes split by the memory they live in: device-local, which the adapter's memory holds
/// (including host-visible device-local memory, the aperture a ring may map, as <see cref="GpuDeviceMemoryWork"/> counts
/// it), and host-visible, which the host's memory holds. Each is a buffer's logical size, not the backend's
/// allocation.</summary>
/// <param name="DeviceLocal">The bytes the adapter's memory holds.</param>
/// <param name="HostVisible">The bytes the host's memory holds.</param>
public readonly record struct GpuMemoryBytes(ulong DeviceLocal, ulong HostVisible) {
    /// <summary>Adds two counts memory by memory.</summary>
    /// <param name="left">One count.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sum.</returns>
    public static GpuMemoryBytes operator +(GpuMemoryBytes left, GpuMemoryBytes right) => new(
        DeviceLocal: checked((left.DeviceLocal + right.DeviceLocal)),
        HostVisible: checked((left.HostVisible + right.HostVisible))
    );
}
