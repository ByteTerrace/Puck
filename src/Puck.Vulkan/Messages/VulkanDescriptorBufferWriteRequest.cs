namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a write of a single buffer descriptor into a descriptor set.
/// </summary>
/// <param name="ArrayElement">The first array element within the binding to update.</param>
/// <param name="Binding">The binding number within the set to update.</param>
/// <param name="BufferHandle">The native <c>VkBuffer</c> handle bound to the descriptor.</param>
/// <param name="BufferOffset">The offset, in bytes, from the start of the buffer at which the bound region begins.</param>
/// <param name="BufferRange">The size, in bytes, of the bound region, or <c>VK_WHOLE_SIZE</c>.</param>
/// <param name="DescriptorSetHandle">The native <c>VkDescriptorSet</c> handle being updated.</param>
/// <param name="DescriptorType">The type of the descriptor, as a <c>VkDescriptorType</c> value.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
public readonly record struct VulkanDescriptorBufferWriteRequest(
    uint ArrayElement,
    uint Binding,
    nint BufferHandle,
    ulong BufferOffset,
    ulong BufferRange,
    nint DescriptorSetHandle,
    uint DescriptorType,
    nint DeviceHandle
);
