using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a region copied and scaled (blitted) from one image to another. Each image's region is given by
/// its subresource and a pair of bounding offsets.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the C arrays srcOffsets[2] / dstOffsets[2] (VkOffset3D[2]) are expanded to indexed fields
/// SrcOffset0/1 and DstOffset0/1 because C# cannot declare a fixed buffer of a struct type. Layout and size (80 B) are
/// identical.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageBlit {
    /// <summary>The subresource of the source image to blit from.</summary>
    public VkImageSubresourceLayers SrcSubresource;
    /// <summary>The first bounding offset of the source region.</summary>
    public VkOffset3D SrcOffset0;
    /// <summary>The second bounding offset of the source region.</summary>
    public VkOffset3D SrcOffset1;
    /// <summary>The subresource of the destination image to blit to.</summary>
    public VkImageSubresourceLayers DstSubresource;
    /// <summary>The first bounding offset of the destination region.</summary>
    public VkOffset3D DstOffset0;
    /// <summary>The second bounding offset of the destination region.</summary>
    public VkOffset3D DstOffset1;
}
