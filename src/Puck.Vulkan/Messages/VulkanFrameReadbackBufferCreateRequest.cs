namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a host-visible readback buffer to create for copying rendered output back to the CPU.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="SizeBytes">The size, in bytes, of the buffer.</param>
public readonly record struct VulkanFrameReadbackBufferCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    ulong SizeBytes
);
