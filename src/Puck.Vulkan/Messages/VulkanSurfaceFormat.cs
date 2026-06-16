namespace Puck.Vulkan.Messages;

/// <summary>
/// A managed view of a surface format/color-space pair (the values of <see cref="Bindings.VkSurfaceFormatKhr"/>).
/// </summary>
/// <param name="Format">The image format, as a <c>VkFormat</c> value.</param>
/// <param name="ColorSpace">The color space, as a <c>VkColorSpaceKHR</c> value.</param>
public readonly record struct VulkanSurfaceFormat(uint Format, uint ColorSpace);
