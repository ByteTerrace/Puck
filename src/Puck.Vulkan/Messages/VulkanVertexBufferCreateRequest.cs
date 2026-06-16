namespace Puck.Vulkan.Messages;

/// <summary>
/// Identifies the device context used to create a vertex buffer. The vertex data and its size are supplied
/// separately to the creating API.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
public readonly record struct VulkanVertexBufferCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle
);
