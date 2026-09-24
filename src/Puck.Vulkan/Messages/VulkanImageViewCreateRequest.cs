using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a 2D image view to create over an existing image.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="Format">The format the view interprets the image with, as a <c>VkFormat</c> value.</param>
/// <param name="ImageHandle">The native <c>VkImage</c> handle the view is created on.</param>
/// <param name="AspectMask">The <c>VkImageAspectFlags</c> the view covers: <see cref="VulkanGpuFormats.ColorAspect"/>
/// for a color image, <see cref="VulkanGpuFormats.DepthAspect"/> for a depth image.</param>
public readonly record struct VulkanImageViewCreateRequest(
    VulkanDeviceCommands Device,
    uint Format,
    nint ImageHandle,
    uint AspectMask = VulkanGpuFormats.ColorAspect
);
