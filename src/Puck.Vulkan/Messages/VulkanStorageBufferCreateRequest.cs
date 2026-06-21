namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a storage buffer to create together with its backing memory.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="SizeBytes">The size, in bytes, of the buffer.</param>
/// <param name="DeviceLocal">When <see langword="true"/>, allocate device-local (not host-visible) backing memory — a GPU-only storage buffer that is never host-mapped; the default is host-visible.</param>
/// <param name="IndirectArgs">When <see langword="true"/>, also set <c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c> so the buffer can back an indirect dispatch/draw.</param>
public readonly record struct VulkanStorageBufferCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    ulong SizeBytes,
    bool DeviceLocal = false,
    bool IndirectArgs = false
);
