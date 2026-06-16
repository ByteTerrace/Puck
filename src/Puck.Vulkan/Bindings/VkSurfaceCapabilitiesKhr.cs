using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the capabilities of a surface relevant to swapchain creation: the supported image counts,
/// extents, layer count, transforms, composite-alpha modes, and image usages.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSurfaceCapabilitiesKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSurfaceCapabilitiesKhr {
    /// <summary>The minimum number of images a swapchain for the surface must contain.</summary>
    public uint MinImageCount;
    /// <summary>The maximum number of images a swapchain for the surface may contain, or zero if there is no limit.</summary>
    public uint MaxImageCount;
    /// <summary>The current width and height of the surface, or <c>(0xFFFFFFFF, 0xFFFFFFFF)</c> if the extent is determined by the swapchain.</summary>
    public VkExtent2D CurrentExtent;
    /// <summary>The smallest valid swapchain image extent for the surface.</summary>
    public VkExtent2D MinImageExtent;
    /// <summary>The largest valid swapchain image extent for the surface.</summary>
    public VkExtent2D MaxImageExtent;
    /// <summary>The maximum number of layers swapchain images can have.</summary>
    public uint MaxImageArrayLayers;
    /// <summary>A bitmask of <c>VkSurfaceTransformFlagBitsKHR</c> values the surface supports.</summary>
    public uint SupportedTransforms;
    /// <summary>The surface's current transform relative to the presentation engine's natural orientation, as a <c>VkSurfaceTransformFlagBitsKHR</c> value.</summary>
    public uint CurrentTransform;
    /// <summary>A bitmask of <c>VkCompositeAlphaFlagBitsKHR</c> values the surface supports.</summary>
    public uint SupportedCompositeAlpha;
    /// <summary>A bitmask of <c>VkImageUsageFlagBits</c> values supported for swapchain images on the surface.</summary>
    public uint SupportedUsageFlags;
}
