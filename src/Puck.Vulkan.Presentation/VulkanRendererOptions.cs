using Puck.Abstractions.Gpu;

namespace Puck.Vulkan.Presentation;

/// <summary>Configuration for the window-bound <see cref="VulkanRenderer"/>.</summary>
public sealed class VulkanRendererOptions {
    /// <summary>The application name reported to the Vulkan instance.</summary>
    public required string ApplicationName { get; init; }
    /// <summary>Gets whether the device's objects are named through <c>VK_EXT_debug_utils</c>, so validation messages
    /// and a GPU capture show each object's name; off, naming formats nothing. The presenter registration sets it from
    /// <c>GpuDeviceOptions.NameObjects</c>.</summary>
    public bool NameObjects { get; init; }
    /// <summary>Gets how much validation the instance loads: above <see cref="GpuValidation.Off"/>, the Khronos
    /// validation layer with synchronization validation and a debug-utils messenger that reports its messages to the
    /// console; at <see cref="GpuValidation.Full"/>, GPU-assisted validation and the best-practices checks too. A
    /// developer diagnostic that adds per-call CPU cost, so it is off by default; the presenter registration sets it from
    /// <c>GpuDeviceOptions.Validation</c>, the peer of the Direct3D 12 debug layer. The debug-utils extension and the
    /// command-buffer labels it carries are enabled independently of this, so GPU-capture debug groups survive a
    /// validation-off run.</summary>
    public GpuValidation Validation { get; init; }
}
