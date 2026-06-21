namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkPresentModeKHR</c> values used when selecting a swapchain present mode. The single source of truth for
/// these spec constants, so the swapchain factory and the renderer's neutral-preference mapping cannot drift apart.
/// </summary>
public static class VulkanPresentMode {
    /// <summary>The <c>VK_PRESENT_MODE_IMMEDIATE_KHR</c> present mode (no vsync; may tear).</summary>
    public const uint Immediate = 0;
    /// <summary>The <c>VK_PRESENT_MODE_MAILBOX_KHR</c> present mode (vsync, latest-frame-wins; low latency).</summary>
    public const uint Mailbox = 1;
    /// <summary>The <c>VK_PRESENT_MODE_FIFO_KHR</c> present mode (vsync; guaranteed always supported).</summary>
    public const uint Fifo = 2;
}
