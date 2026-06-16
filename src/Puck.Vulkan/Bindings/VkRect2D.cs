using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A two-dimensional, axis-aligned subregion defined by an integer <see cref="Offset"/> (its upper-left
/// corner) and an <see cref="Extent"/> (its size), used for render areas, scissor rectangles, and the like.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkRect2D (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkRect2D(VkOffset2D offset, VkExtent2D extent) {
    /// <summary>The coordinates of the upper-left corner of the region.</summary>
    public readonly VkOffset2D Offset = offset;
    /// <summary>The width and height of the region.</summary>
    public readonly VkExtent2D Extent = extent;
}
