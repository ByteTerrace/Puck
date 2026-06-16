namespace Puck.Vulkan.Messages;

/// <summary>
/// Specifies how many descriptors of a given type a descriptor pool should reserve.
/// </summary>
/// <param name="DescriptorCount">The number of descriptors of <paramref name="DescriptorType"/> to reserve.</param>
/// <param name="DescriptorType">The type of descriptors counted, as a <c>VkDescriptorType</c> value.</param>
public readonly record struct VulkanDescriptorPoolSize(
    uint DescriptorCount,
    uint DescriptorType
);
