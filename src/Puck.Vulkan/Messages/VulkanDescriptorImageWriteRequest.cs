namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a write of a single image (and optionally sampler) descriptor into a descriptor set.
/// </summary>
/// <param name="ArrayElement">The first array element within the binding to update.</param>
/// <param name="Binding">The binding number within the set to update.</param>
/// <param name="DescriptorSetHandle">The native <c>VkDescriptorSet</c> handle being updated.</param>
/// <param name="DescriptorType">The type of the descriptor, as a <c>VkDescriptorType</c> value.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="ImageLayout">The layout the image is in at access time, as a <c>VkImageLayout</c> value.</param>
/// <param name="ImageViewHandle">The native <c>VkImageView</c> handle bound to the descriptor.</param>
/// <param name="SamplerHandle">The native <c>VkSampler</c> handle bound to the descriptor, or zero when the type uses no sampler.</param>
public readonly record struct VulkanDescriptorImageWriteRequest(
    uint ArrayElement,
    uint Binding,
    nint DescriptorSetHandle,
    uint DescriptorType,
    nint DeviceHandle,
    uint ImageLayout,
    nint ImageViewHandle,
    nint SamplerHandle
);
