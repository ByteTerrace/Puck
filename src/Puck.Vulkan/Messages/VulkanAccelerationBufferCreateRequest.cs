namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a buffer to create for acceleration structure storage or build scratch.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="SizeBytes">The size, in bytes, of the buffer.</param>
/// <param name="Usage">A bitmask of <c>VkBufferUsageFlagBits</c> describing the intended usage of the buffer.</param>
/// <param name="HostVisible">Whether the buffer is allocated from host-visible memory.</param>
public readonly record struct VulkanAccelerationBufferCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    ulong SizeBytes,
    uint Usage,
    bool HostVisible
);
