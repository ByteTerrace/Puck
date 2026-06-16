namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkFilter</c> values used when creating samplers.
/// </summary>
public static class VulkanFilter {
    /// <summary>The <c>VK_FILTER_NEAREST</c> value.</summary>
    public const uint Nearest = 0;
    /// <summary>The <c>VK_FILTER_LINEAR</c> value.</summary>
    public const uint Linear = 1;
}
