namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a host-visible storage buffer to create together with its backing memory.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="SizeBytes">The size, in bytes, of the buffer.</param>
public readonly record struct VulkanStorageBufferCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    ulong SizeBytes
);
