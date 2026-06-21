namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes the device resources to create for the ray-query world path: the handles needed to allocate the
/// acceleration-structure buffers and the maximum number of instances the persistent instance buffer reserves.
/// </summary>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support and scratch alignment.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle the resources are created on.</param>
/// <param name="MaxInstanceCount">The maximum number of instances the persistent instance buffer reserves capacity for.</param>
public readonly record struct VulkanWorldAccelerationCreateRequest(
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    nint DeviceHandle,
    uint MaxInstanceCount
);
