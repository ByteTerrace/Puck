namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a write of a single top-level acceleration structure (TLAS) descriptor into a descriptor set. The
/// acceleration structure handle rides a <c>VkWriteDescriptorSetAccelerationStructureKHR</c> chained into the
/// write's <c>pNext</c> pointer rather than a buffer or image info array. Vulkan-only (no Direct3D 12 equivalent).
/// </summary>
/// <param name="AccelerationStructureHandle">The native <c>VkAccelerationStructureKHR</c> handle bound to the descriptor.</param>
/// <param name="Binding">The binding number within the set to update.</param>
/// <param name="DescriptorSetHandle">The native <c>VkDescriptorSet</c> handle being updated.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
public readonly record struct VulkanDescriptorAccelerationStructureWriteRequest(
    nint AccelerationStructureHandle,
    uint Binding,
    nint DescriptorSetHandle,
    nint DeviceHandle
);
