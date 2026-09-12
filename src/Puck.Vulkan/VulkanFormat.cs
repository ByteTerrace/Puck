namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkFormat</c> values used when creating images, image views, and offscreen render targets.
/// </summary>
public static class VulkanFormat {
    /// <summary>The <c>VK_FORMAT_R8G8B8A8_UNORM</c> format.</summary>
    public const uint R8G8B8A8Unorm = 37;
    /// <summary>The <c>VK_FORMAT_B8G8R8A8_UNORM</c> format.</summary>
    public const uint B8G8R8A8Unorm = 44;
    /// <summary>The <c>VK_FORMAT_R16G16B16A16_SFLOAT</c> value.</summary>
    public const uint R16G16B16A16Sfloat = 97;
    /// <summary>The <c>VK_FORMAT_R32G32B32A32_SFLOAT</c> value.</summary>
    public const uint R32G32B32A32Sfloat = 109;
}
