using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a batch of primary command buffers to allocate from a command pool.
/// </summary>
/// <param name="CommandPoolHandle">The native <c>VkCommandPool</c> handle to allocate from.</param>
/// <param name="CommandBufferCount">The number of command buffers to allocate.</param>
/// <param name="Device">The command table of the logical device.</param>
public readonly record struct VulkanCommandBufferAllocateRequest(
    nint CommandPoolHandle,
    uint CommandBufferCount,
    VulkanDeviceCommands Device
);
