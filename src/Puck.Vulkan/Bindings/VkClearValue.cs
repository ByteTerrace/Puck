using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The clear value used for a color, depth, or stencil attachment. Only the color view is represented by
/// this binding (see remarks); the appropriate view is selected by the attachment's aspect at clear time.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): VkClearValue is a C union of { VkClearColorValue color; VkClearDepthStencilValue depthStencil
/// }. Only 'color' (the larger, 16-B member) is bound, which fixes the union size. A depth/stencil clear occupies the
/// union's first eight bytes, a float depth then a uint32 stencil, which <see cref="OfDepth"/> writes through the color
/// member's first two lanes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkClearValue {
    /// <summary>The color image clear value.</summary>
    public VkClearColorValue Color;

    /// <summary>Creates the clear value of a color attachment.</summary>
    /// <param name="red">The red channel.</param>
    /// <param name="green">The green channel.</param>
    /// <param name="blue">The blue channel.</param>
    /// <param name="alpha">The alpha channel.</param>
    /// <returns>The clear value.</returns>
    public static VkClearValue OfColor(float red, float green, float blue, float alpha) => new() {
        Color = new VkClearColorValue(
            float32_0: red,
            float32_1: green,
            float32_2: blue,
            float32_3: alpha
        ),
    };
    /// <summary>Creates the clear value of a depth attachment with a zero stencil: the union's depth member, a float
    /// depth followed by a zero uint32 stencil, which a zero float lane reproduces bit for bit.</summary>
    /// <param name="depth">The depth.</param>
    /// <returns>The clear value.</returns>
    public static VkClearValue OfDepth(float depth) => new() {
        Color = new VkClearColorValue(
            float32_0: depth,
            float32_1: 0f,
            float32_2: 0f,
            float32_3: 0f
        ),
    };
}
