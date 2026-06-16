namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a single descriptor set to allocate: the pool to allocate from and the layout of the set.
/// </summary>
/// <param name="DescriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle describing the set.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="PoolHandle">The native <c>VkDescriptorPool</c> handle to allocate from.</param>
public readonly record struct VulkanDescriptorSetAllocateRequest(
    nint DescriptorSetLayoutHandle,
    nint DeviceHandle,
    nint PoolHandle
);
