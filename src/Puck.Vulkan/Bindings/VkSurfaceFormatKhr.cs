using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A format/color-space pair supported by a surface for swapchain images.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSurfaceFormatKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSurfaceFormatKhr {
    /// <summary>The image format compatible with the surface, as a <c>VkFormat</c> value.</summary>
    public uint Format;
    /// <summary>The color space compatible with the surface, as a <c>VkColorSpaceKHR</c> value.</summary>
    public uint ColorSpace;
}
