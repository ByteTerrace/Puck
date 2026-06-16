using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A three-dimensional, signed integer offset giving the x, y, and z coordinates of a point relative to an origin.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkOffset3D (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkOffset3D(int x, int y, int z) {
    /// <summary>The x offset.</summary>
    public readonly int X = x;
    /// <summary>The y offset.</summary>
    public readonly int Y = y;
    /// <summary>The z offset.</summary>
    public readonly int Z = z;
}
