using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A viewport, defining the affine transform from normalized device coordinates to framebuffer
/// coordinates. <see cref="X"/>/<see cref="Y"/> and <see cref="Width"/>/<see cref="Height"/> give the
/// viewport's pixel rectangle, while <see cref="MinDepth"/>/<see cref="MaxDepth"/> give the depth range
/// that the clip-space z is mapped into.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkViewport (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VkViewport(float x, float y, float width, float height, float minDepth, float maxDepth) {
    /// <summary>The x coordinate of the viewport's upper-left corner, in pixels.</summary>
    public readonly float X = x;
    /// <summary>The y coordinate of the viewport's upper-left corner, in pixels.</summary>
    public readonly float Y = y;
    /// <summary>The width of the viewport, in pixels.</summary>
    public readonly float Width = width;
    /// <summary>The height of the viewport, in pixels. May be negative to flip the y axis.</summary>
    public readonly float Height = height;
    /// <summary>The minimum depth of the viewport, the framebuffer depth that clip-space z maps onto. Normally in <c>[0, 1]</c>.</summary>
    public readonly float MinDepth = minDepth;
    /// <summary>The maximum depth of the viewport, the framebuffer depth that clip-space z maps onto. Normally in <c>[0, 1]</c>.</summary>
    public readonly float MaxDepth = maxDepth;
}
