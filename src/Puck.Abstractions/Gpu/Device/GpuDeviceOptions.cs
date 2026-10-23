namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral device-creation choices a host registers before a backend creates its device. Both backends read
/// it at device creation: the Vulkan presenter's renderer options and the Direct3D 12 device context. Absent, every
/// choice is off.
/// </summary>
public sealed class GpuDeviceOptions {
    /// <summary>Gets how much of its backend's validation the device is created with (<see cref="GpuValidation"/>). A
    /// developer and qualification diagnostic that adds per-call CPU cost, and at <see cref="GpuValidation.Full"/> GPU
    /// cost too; on some Direct3D 12 configurations the layer makes device creation fail, and it cannot be turned off
    /// once a process has enabled it. The World sets it from its <c>--debug-layers [basic|full]</c> flag.</summary>
    public GpuValidation Validation { get; init; }
    /// <summary>Gets whether the device names its objects (<see cref="GpuObjectNaming"/>), so a validation message, a
    /// device-removal report or a GPU capture shows each object's name. The World turns it on with any validation level
    /// and with a capture tool (<c>--capture-tool</c>); off, naming formats nothing.</summary>
    public bool NameObjects { get; init; }
    /// <summary>Gets whether the device is created on the platform's software rasterizer rather than a hardware adapter:
    /// WARP on Direct3D 12, which runs on the CPU on a host with or without a GPU. Vulkan has no software device here and
    /// refuses the option. The World sets it from its <c>--software-gpu</c> flag.</summary>
    public bool Software { get; init; }
}
