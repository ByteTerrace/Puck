namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a descriptor pool to create: the maximum number of sets it can allocate and its per-type
/// descriptor capacity.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="Flags">A bitmask of <c>VkDescriptorPoolCreateFlagBits</c> specifying behavior of the pool.</param>
/// <param name="MaxSets">The maximum number of descriptor sets that can be allocated from the pool.</param>
/// <param name="PoolSizes">The per-type descriptor capacity of the pool.</param>
public readonly record struct VulkanDescriptorPoolCreateRequest(
    nint DeviceHandle,
    uint Flags,
    uint MaxSets,
    ReadOnlyMemory<VulkanDescriptorPoolSize> PoolSizes
);
