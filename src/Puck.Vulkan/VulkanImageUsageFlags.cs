namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkImageUsageFlagBits</c> values used when creating images. Combine with bitwise OR.
/// </summary>
public static class VulkanImageUsageFlags {
    /// <summary>The <c>VK_IMAGE_USAGE_TRANSFER_SRC_BIT</c> value.</summary>
    public const uint TransferSource = 0x00000001;
    /// <summary>The <c>VK_IMAGE_USAGE_TRANSFER_DST_BIT</c> value.</summary>
    public const uint TransferDestination = 0x00000002;
    /// <summary>The <c>VK_IMAGE_USAGE_SAMPLED_BIT</c> value.</summary>
    public const uint Sampled = 0x00000004;
    /// <summary>The <c>VK_IMAGE_USAGE_STORAGE_BIT</c> value.</summary>
    public const uint Storage = 0x00000008;
    /// <summary>The <c>VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT</c> value.</summary>
    public const uint ColorAttachment = 0x00000010;
}
