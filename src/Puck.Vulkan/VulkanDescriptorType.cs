namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkDescriptorType</c> values used when sizing descriptor pools and writing descriptor sets.
/// </summary>
public static class VulkanDescriptorType {
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_SAMPLER</c> value.</summary>
    public const uint Sampler = 0;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER</c> value.</summary>
    public const uint CombinedImageSampler = 1;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE</c> value.</summary>
    public const uint SampledImage = 2;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_STORAGE_IMAGE</c> value.</summary>
    public const uint StorageImage = 3;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER</c> value.</summary>
    public const uint UniformBuffer = 6;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER</c> value.</summary>
    public const uint StorageBuffer = 7;
}
