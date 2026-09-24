namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral device-creation choices a host registers before a backend creates its device. Both backends read
/// it at device creation: the Vulkan presenter's renderer options and the Direct3D 12 device context. Absent, every
/// choice is off.
/// </summary>
public sealed class GpuDeviceOptions {
    /// <summary>Gets whether the device is created with its backend's validation layer: the Khronos validation layer
    /// and a messenger printing <c>[vulkan-debug]</c> lines on Vulkan, the debug layer and its <c>[d3d12-debug]</c> drain
    /// and teardown report on Direct3D 12. A developer and qualification diagnostic that adds per-call CPU cost; on
    /// some Direct3D 12 configurations the layer makes device creation fail, and it cannot be turned off once a process
    /// has enabled it. The World sets it from its <c>--debug-layers</c> flag.</summary>
    public bool DebugLayers { get; init; }
}
