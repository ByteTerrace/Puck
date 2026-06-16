namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkImageLayout</c> values used in layout transitions, render passes, and descriptor writes.
/// </summary>
public static class VulkanImageLayout {
    /// <summary>The <c>VK_IMAGE_LAYOUT_UNDEFINED</c> value.</summary>
    public const uint Undefined = 0;
    /// <summary>The <c>VK_IMAGE_LAYOUT_GENERAL</c> value.</summary>
    public const uint General = 1;
    /// <summary>The <c>VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL</c> value.</summary>
    public const uint ShaderReadOnlyOptimal = 5;
    /// <summary>The <c>VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL</c> value.</summary>
    public const uint TransferSourceOptimal = 6;
    /// <summary>The <c>VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL</c> value.</summary>
    public const uint TransferDestinationOptimal = 7;
    /// <summary>The <c>VK_IMAGE_LAYOUT_PRESENT_SRC_KHR</c> value.</summary>
    public const uint PresentSourceKhr = 1000001002;
}
