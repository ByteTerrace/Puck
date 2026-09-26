namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkFormat</c> values used when creating images, image views, and offscreen render targets.
/// </summary>
public static class VulkanFormat {
    /// <summary>The <c>VK_FORMAT_B8G8R8A8_UNORM</c> format.</summary>
    public const uint B8G8R8A8Unorm = 44;
    /// <summary>The <c>VK_FORMAT_BC4_UNORM_BLOCK</c> value.</summary>
    public const uint Bc4UnormBlock = 139;
    /// <summary>The <c>VK_FORMAT_BC5_UNORM_BLOCK</c> value.</summary>
    public const uint Bc5UnormBlock = 141;
    /// <summary>The <c>VK_FORMAT_BC6H_UFLOAT_BLOCK</c> value.</summary>
    public const uint Bc6hUfloatBlock = 143;
    /// <summary>The <c>VK_FORMAT_BC7_UNORM_BLOCK</c> value.</summary>
    public const uint Bc7UnormBlock = 145;
    /// <summary>The <c>VK_FORMAT_D32_SFLOAT</c> value.</summary>
    public const uint D32Sfloat = 126;
    /// <summary>The <c>VK_FORMAT_R16G16B16A16_SFLOAT</c> value.</summary>
    public const uint R16G16B16A16Sfloat = 97;
    /// <summary>The <c>VK_FORMAT_R32G32B32A32_SFLOAT</c> value.</summary>
    public const uint R32G32B32A32Sfloat = 109;
    /// <summary>The <c>VK_FORMAT_R8G8B8A8_UNORM</c> format.</summary>
    public const uint R8G8B8A8Unorm = 37;
}
