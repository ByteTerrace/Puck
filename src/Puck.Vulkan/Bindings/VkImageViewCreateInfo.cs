using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing an image view to be created with <c>vkCreateImageView</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageViewCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageViewCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkImageViewCreateFlagBits</c> specifying additional parameters of the view.</summary>
    public uint Flags;
    /// <summary>The image on which the view is created (a <c>VkImage</c> handle).</summary>
    public nint Image;
    /// <summary>The type of the image view (for example 1D, 2D, 3D, or cube), as a <c>VkImageViewType</c> value.</summary>
    public uint ViewType;
    /// <summary>The format and type by which the view interprets the image's texels, as a <c>VkFormat</c> value.</summary>
    public uint Format;
    /// <summary>A remapping of the image's color components applied by the view.</summary>
    public VkComponentMapping Components;
    /// <summary>The range of image subresources (aspects, mip levels, array layers) the view exposes.</summary>
    public VkImageSubresourceRange SubresourceRange;
}
