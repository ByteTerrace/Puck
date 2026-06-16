namespace Puck.Launcher.Vulkan;

/// <summary>Configuration for the window-bound <see cref="VulkanRenderer"/>.</summary>
public sealed class VulkanRendererOptions {
    /// <summary>The application name reported to the Vulkan instance.</summary>
    public required string ApplicationName { get; init; }
}
