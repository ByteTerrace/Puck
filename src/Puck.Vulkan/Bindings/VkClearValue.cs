using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// The clear value used for a color, depth, or stencil attachment. Only the color view is represented by
/// this binding (see remarks); the appropriate view is selected by the attachment's aspect at clear time.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): VkClearValue is a C union of { VkClearColorValue color; VkClearDepthStencilValue depthStencil
/// }. Only 'color' (the larger, 16-B member) is bound, which fixes the union size; depth/stencil clears are not
/// represented.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkClearValue {
    /// <summary>The color image clear value.</summary>
    public VkClearColorValue Color;
}
