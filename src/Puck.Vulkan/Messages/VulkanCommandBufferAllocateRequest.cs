namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a batch of primary command buffers to allocate from a command pool.
/// </summary>
/// <param name="CommandPoolHandle">The native <c>VkCommandPool</c> handle to allocate from.</param>
/// <param name="CommandBufferCount">The number of command buffers to allocate.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
public readonly record struct VulkanCommandBufferAllocateRequest(
    nint CommandPoolHandle,
    uint CommandBufferCount,
    nint DeviceHandle
);
