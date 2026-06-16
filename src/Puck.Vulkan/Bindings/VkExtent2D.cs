using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A two-dimensional extent, giving the width and height of a region. The unit is defined by the
/// consuming structure (typically pixels for surfaces and framebuffers, or texels for images).
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkExtent2D (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkExtent2D(uint width, uint height) {
    /// <summary>The width of the extent.</summary>
    public readonly uint Width = width;
    /// <summary>The height of the extent.</summary>
    public readonly uint Height = height;
}
