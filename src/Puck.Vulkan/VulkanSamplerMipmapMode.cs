namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkSamplerMipmapMode</c> values used when creating samplers.
/// </summary>
public static class VulkanSamplerMipmapMode {
    /// <summary>The <c>VK_SAMPLER_MIPMAP_MODE_NEAREST</c> value.</summary>
    public const uint Nearest = 0;
    /// <summary>The <c>VK_SAMPLER_MIPMAP_MODE_LINEAR</c> value.</summary>
    public const uint Linear = 1;
}
