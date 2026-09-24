using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes an offscreen color image to create together with its backing memory.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="Instance">The command table of the instance, used to resolve memory support.</param>
/// <param name="PhysicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle, used to resolve memory support.</param>
/// <param name="Width">The width, in texels, of the image.</param>
/// <param name="Height">The height, in texels, of the image.</param>
/// <param name="Format">The image format, as a <c>VkFormat</c> value.</param>
/// <param name="UsageFlags">A bitmask of <c>VkImageUsageFlagBits</c> describing the intended usage of the image.</param>
public readonly record struct VulkanOffscreenImageCreateRequest(
    VulkanDeviceCommands Device,
    VulkanInstanceCommands Instance,
    nint PhysicalDeviceHandle,
    uint Width,
    uint Height,
    uint Format,
    uint UsageFlags
);
