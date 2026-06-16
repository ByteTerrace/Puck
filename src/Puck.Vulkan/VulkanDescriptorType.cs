namespace Puck.Vulkan;

/// <summary>
/// Common <c>VkDescriptorType</c> values used when sizing descriptor pools and writing descriptor sets.
/// </summary>
public static class VulkanDescriptorType {
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER</c> value.</summary>
    public const uint CombinedImageSampler = 1;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_STORAGE_IMAGE</c> value.</summary>
    public const uint StorageImage = 3;
    /// <summary>The <c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER</c> value.</summary>
    public const uint StorageBuffer = 7;
}
