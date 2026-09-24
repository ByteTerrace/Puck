namespace Puck.Vulkan.Presentation;

/// <summary>Configuration for the window-bound <see cref="VulkanRenderer"/>.</summary>
public sealed class VulkanRendererOptions {
    /// <summary>The application name reported to the Vulkan instance.</summary>
    public required string ApplicationName { get; init; }
    /// <summary>Whether the instance loads the Khronos validation layer and registers a debug-utils messenger that
    /// reports its messages to the console. A developer diagnostic that adds per-call CPU cost, so it is off by default;
    /// the presenter registration sets it from <c>GpuDeviceOptions.DebugLayers</c>, the peer of the Direct3D 12 debug
    /// layer. The debug-utils extension and the command-buffer labels it carries are enabled independently of this, so
    /// GPU-capture debug groups survive a validation-off run.</summary>
    public bool EnableValidation { get; init; }
}
