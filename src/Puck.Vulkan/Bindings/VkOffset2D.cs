using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A two-dimensional, signed integer offset giving the x and y coordinates of a point relative to an origin.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkOffset2D (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkOffset2D(int x, int y) {
    /// <summary>The x offset.</summary>
    public readonly int X = x;
    /// <summary>The y offset.</summary>
    public readonly int Y = y;
}
