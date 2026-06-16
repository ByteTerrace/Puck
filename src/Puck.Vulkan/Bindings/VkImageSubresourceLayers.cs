using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Selects the image subresources (aspects, a single mip level, and a range of array layers) acted on by
/// an image copy, blit, or resolve.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageSubresourceLayers (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageSubresourceLayers {
    /// <summary>A bitmask of <c>VkImageAspectFlagBits</c> selecting the aspect(s) of the image to copy.</summary>
    public uint AspectMask;
    /// <summary>The mipmap level to copy.</summary>
    public uint MipLevel;
    /// <summary>The first array layer to copy.</summary>
    public uint BaseArrayLayer;
    /// <summary>The number of array layers to copy, starting from <see cref="BaseArrayLayer"/>.</summary>
    public uint LayerCount;
}
