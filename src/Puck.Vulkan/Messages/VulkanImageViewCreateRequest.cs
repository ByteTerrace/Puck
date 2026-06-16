namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a 2D color image view to create over an existing image.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="Format">The format the view interprets the image with, as a <c>VkFormat</c> value.</param>
/// <param name="ImageHandle">The native <c>VkImage</c> handle the view is created on.</param>
public readonly record struct VulkanImageViewCreateRequest(
    nint DeviceHandle,
    uint Format,
    nint ImageHandle
);
