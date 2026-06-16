using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// An image memory barrier used in a pipeline barrier: it scopes a memory dependency for an image
/// subresource range and can perform a layout transition and/or a queue family ownership transfer.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkImageMemoryBarrier (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkImageMemoryBarrier {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the source access scope of the barrier.</summary>
    public uint SrcAccessMask;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope of the barrier.</summary>
    public uint DstAccessMask;
    /// <summary>The layout the image subresource range is in before the barrier, as a <c>VkImageLayout</c> value.</summary>
    public uint OldLayout;
    /// <summary>The layout the image subresource range is transitioned to by the barrier, as a <c>VkImageLayout</c> value.</summary>
    public uint NewLayout;
    /// <summary>The source queue family for an ownership transfer, or <c>VK_QUEUE_FAMILY_IGNORED</c> when no transfer occurs.</summary>
    public uint SrcQueueFamilyIndex;
    /// <summary>The destination queue family for an ownership transfer, or <c>VK_QUEUE_FAMILY_IGNORED</c> when no transfer occurs.</summary>
    public uint DstQueueFamilyIndex;
    /// <summary>The image affected by the barrier (a <c>VkImage</c> handle).</summary>
    public nint Image;
    /// <summary>The subresource range of <see cref="Image"/> affected by the barrier.</summary>
    public VkImageSubresourceRange SubresourceRange;
}
