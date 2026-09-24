namespace Puck.Abstractions.Gpu;

/// <summary>
/// How a <see cref="GpuRegion"/>'s bytes reach the GPU work that reads them. <see cref="GpuResidency.Select"/> chooses
/// one from the device's <see cref="GpuMemoryProfile"/> and the region's size; every policy leaves the same bytes where
/// the GPU reads them.
/// </summary>
public enum GpuResidencyPolicy {
    /// <summary>The host writes a host-visible staging buffer per frame slot, and one compute copy per frame moves the
    /// changed words into a device-local buffer the GPU reads.</summary>
    Staged = 0,
    /// <summary>The host writes one host-visible buffer per frame slot, and the GPU reads the slot's buffer directly.</summary>
    Ring = 1,
    /// <summary>The host writes the one buffer the GPU reads, in place, on coherent unified memory.</summary>
    InPlace = 2,
}
