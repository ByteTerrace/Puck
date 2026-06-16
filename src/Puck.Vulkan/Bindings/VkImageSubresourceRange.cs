using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Selects a range of image subresources — the aspects, a contiguous range of mip levels, and a
/// contiguous range of array layers — addressed by an image view or an image memory barrier.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageSubresourceRange (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageSubresourceRange {
    /// <summary>A bitmask of <c>VkImageAspectFlagBits</c> selecting the aspect(s) of the image included in the range.</summary>
    public uint AspectMask;
    /// <summary>The first mipmap level accessible to the range.</summary>
    public uint BaseMipLevel;
    /// <summary>The number of mipmap levels, starting from <see cref="BaseMipLevel"/>, accessible to the range.</summary>
    public uint LevelCount;
    /// <summary>The first array layer accessible to the range.</summary>
    public uint BaseArrayLayer;
    /// <summary>The number of array layers, starting from <see cref="BaseArrayLayer"/>, accessible to the range.</summary>
    public uint LayerCount;
}
