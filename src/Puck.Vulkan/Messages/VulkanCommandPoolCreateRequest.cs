namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a command pool to create for a given queue family.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="QueueFamilyIndex">The queue family that command buffers from the pool are submitted to.</param>
public readonly record struct VulkanCommandPoolCreateRequest(nint DeviceHandle, uint QueueFamilyIndex);
