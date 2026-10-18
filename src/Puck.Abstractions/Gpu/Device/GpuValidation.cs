namespace Puck.Abstractions.Gpu;

/// <summary>
/// How much of its backend's validation a device is created with: the World's <c>--debug-layers [basic|full]</c>. Each
/// level adds to the one before it, and every level above <see cref="Off"/> names the device's objects.
/// </summary>
public enum GpuValidation {
    /// <summary>No validation layer: the default, and what every shipped run uses.</summary>
    Off = 0,
    /// <summary>The API validation layer: the Khronos validation layer with synchronization validation and a messenger
    /// printing <c>[vulkan-debug]</c> lines on Vulkan; the debug layer, its <c>[d3d12-debug]</c> drain and teardown
    /// live-object report on Direct3D 12. A bare <c>--debug-layers</c> asks for it, and every verb that boots a World
    /// under its layer (<c>puck canary</c>, <c>puck parity</c>, <c>puck qualify</c>) uses it.</summary>
    Basic = 1,
    /// <summary><see cref="Basic"/>, plus what checks the GPU's own work and advises on cost: GPU-based validation with
    /// state tracking and synchronized command-queue validation on Direct3D 12; GPU-assisted validation and the
    /// best-practices checks, NVIDIA's included, on Vulkan. Shader instrumentation makes pipeline creation and every
    /// frame much slower, so it is a hunting level, never a gate's.</summary>
    Full = 2,
}
