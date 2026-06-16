namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes an offscreen color image to create together with its backing memory.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="InstanceHandle">The native <c>VkInstance</c> handle, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="Width">The width, in texels, of the image.</param>
/// <param name="Height">The height, in texels, of the image.</param>
/// <param name="Format">The image format, as a <c>VkFormat</c> value.</param>
/// <param name="UsageFlags">A bitmask of <c>VkImageUsageFlagBits</c> describing the intended usage of the image.</param>
public readonly record struct VulkanOffscreenImageCreateRequest(
    nint DeviceHandle,
    nint InstanceHandle,
    nint PhysicalDeviceHandle,
    uint Width,
    uint Height,
    uint Format,
    uint UsageFlags
);
