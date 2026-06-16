using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the features a physical device supports for a given format, separately for linearly tiled
/// images, optimally tiled images, and buffers.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkFormatProperties (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkFormatProperties {
    /// <summary>A bitmask of <c>VkFormatFeatureFlagBits</c> supported by images created with linear tiling.</summary>
    public uint LinearTilingFeatures;
    /// <summary>A bitmask of <c>VkFormatFeatureFlagBits</c> supported by images created with optimal tiling.</summary>
    public uint OptimalTilingFeatures;
    /// <summary>A bitmask of <c>VkFormatFeatureFlagBits</c> supported by buffers using the format.</summary>
    public uint BufferFeatures;
}
