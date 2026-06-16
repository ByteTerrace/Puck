using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing an image to be created with <c>vkCreateImage</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkImageCreateFlagBits</c> specifying additional parameters of the image.</summary>
    public uint Flags;
    /// <summary>The basic dimensionality of the image, as a <c>VkImageType</c> value.</summary>
    public uint ImageType;
    /// <summary>The format and type of the texel blocks the image holds, as a <c>VkFormat</c> value.</summary>
    public uint Format;
    /// <summary>The number of texels in each dimension of the image.</summary>
    public VkExtent3D Extent;
    /// <summary>The number of levels of detail available for minified sampling of the image.</summary>
    public uint MipLevels;
    /// <summary>The number of layers in the image.</summary>
    public uint ArrayLayers;
    /// <summary>The number of samples per texel, as a <c>VkSampleCountFlagBits</c> value.</summary>
    public uint Samples;
    /// <summary>The tiling arrangement of the texel blocks in memory, as a <c>VkImageTiling</c> value.</summary>
    public uint Tiling;
    /// <summary>A bitmask of <c>VkImageUsageFlagBits</c> specifying the intended usage of the image.</summary>
    public uint Usage;
    /// <summary>The sharing mode used when the image is accessed by multiple queue families, as a <c>VkSharingMode</c> value.</summary>
    public uint SharingMode;
    /// <summary>The number of entries in the <see cref="PQueueFamilyIndices"/> array. Used only when <see cref="SharingMode"/> is concurrent.</summary>
    public uint QueueFamilyIndexCount;
    /// <summary>A pointer to an array of queue family indices that will access the image. Used only when <see cref="SharingMode"/> is concurrent.</summary>
    public nint PQueueFamilyIndices;
    /// <summary>The layout of all image subresources when the image is created, as a <c>VkImageLayout</c> value.</summary>
    public uint InitialLayout;
}
