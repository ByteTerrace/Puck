using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A remapping of the color components of an image view. Each field is a <c>VkComponentSwizzle</c> value
/// selecting which source component (or constant) supplies that output component, allowing channels to be
/// reordered, duplicated, or forced to zero/one.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkComponentMapping (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkComponentMapping {
    /// <summary>The swizzle that determines the value placed in the red component of the view, as a <c>VkComponentSwizzle</c> value.</summary>
    public uint R;
    /// <summary>The swizzle that determines the value placed in the green component of the view, as a <c>VkComponentSwizzle</c> value.</summary>
    public uint G;
    /// <summary>The swizzle that determines the value placed in the blue component of the view, as a <c>VkComponentSwizzle</c> value.</summary>
    public uint B;
    /// <summary>The swizzle that determines the value placed in the alpha component of the view, as a <c>VkComponentSwizzle</c> value.</summary>
    public uint A;
}
