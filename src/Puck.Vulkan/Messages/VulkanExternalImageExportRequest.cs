namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a Vulkan image to create in exportable device memory, whose backing memory can be shared with another
/// Vulkan instance through an opaque Win32 NT handle.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="Width">The image width, in texels.</param>
/// <param name="Height">The image height, in texels.</param>
/// <param name="Format">The image format, as a <c>VkFormat</c> value.</param>
/// <param name="UsageFlags">A bitmask of <c>VkImageUsageFlagBits</c> describing the intended usage (a render target wants COLOR_ATTACHMENT | SAMPLED; a storage image wants STORAGE | SAMPLED).</param>
public readonly record struct VulkanExternalImageExportRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    uint Width,
    uint Height,
    uint Format,
    uint UsageFlags
);
