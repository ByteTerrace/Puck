using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a region copied between a buffer and an image: the buffer location and memory layout, and the
/// image subresource, offset, and extent.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkBufferImageCopy (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkBufferImageCopy {
    /// <summary>The offset, in bytes, from the start of the buffer at which the image data begins.</summary>
    public ulong BufferOffset;
    /// <summary>The number of texels in a row of the buffer's image layout, or zero to use <see cref="ImageExtent"/>'s width (tightly packed).</summary>
    public uint BufferRowLength;
    /// <summary>The number of rows in a layer of the buffer's image layout, or zero to use <see cref="ImageExtent"/>'s height (tightly packed).</summary>
    public uint BufferImageHeight;
    /// <summary>The image subresource (aspect, mip level, array layers) to copy.</summary>
    public VkImageSubresourceLayers ImageSubresource;
    /// <summary>The initial texel offset, in the image, of the region.</summary>
    public VkOffset3D ImageOffset;
    /// <summary>The size, in texels, of the region.</summary>
    public VkExtent3D ImageExtent;
}
