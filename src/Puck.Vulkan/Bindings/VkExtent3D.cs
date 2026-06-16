using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A three-dimensional extent, giving the width, height, and depth of a region. The unit is defined by
/// the consuming structure (typically texels for images).
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkExtent3D (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkExtent3D(uint width, uint height, uint depth) {
    /// <summary>The width of the extent.</summary>
    public readonly uint Width = width;
    /// <summary>The height of the extent.</summary>
    public readonly uint Height = height;
    /// <summary>The depth of the extent.</summary>
    public readonly uint Depth = depth;
}
