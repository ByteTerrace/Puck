namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkSamplerAddressMode</c> values used when creating samplers.
/// </summary>
public static class VulkanSamplerAddressMode {
    /// <summary>The <c>VK_SAMPLER_ADDRESS_MODE_REPEAT</c> value.</summary>
    public const uint Repeat = 0;
    /// <summary>The <c>VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE</c> value.</summary>
    public const uint ClampToEdge = 2;
}
