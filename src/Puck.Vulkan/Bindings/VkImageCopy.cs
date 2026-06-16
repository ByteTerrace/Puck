using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a region copied from one image to another: the source and destination subresources and
/// offsets, and the shared extent.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageCopy (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageCopy {
    /// <summary>The subresource of the source image to copy from.</summary>
    public VkImageSubresourceLayers SrcSubresource;
    /// <summary>The initial texel offset, in the source image, of the region.</summary>
    public VkOffset3D SrcOffset;
    /// <summary>The subresource of the destination image to copy to.</summary>
    public VkImageSubresourceLayers DstSubresource;
    /// <summary>The initial texel offset, in the destination image, of the region.</summary>
    public VkOffset3D DstOffset;
    /// <summary>The size, in texels, of the region copied.</summary>
    public VkExtent3D Extent;
}
