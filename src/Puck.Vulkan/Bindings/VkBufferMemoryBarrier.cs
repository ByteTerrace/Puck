using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A buffer memory barrier used in a pipeline barrier: it scopes a memory dependency to one range of one buffer.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkBufferMemoryBarrier (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkBufferMemoryBarrier {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the source access scope of the barrier.</summary>
    public uint SrcAccessMask;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope of the barrier.</summary>
    public uint DstAccessMask;
    /// <summary>The source queue family of a queue family ownership transfer, or <c>VK_QUEUE_FAMILY_IGNORED</c>.</summary>
    public uint SrcQueueFamilyIndex;
    /// <summary>The destination queue family of a queue family ownership transfer, or <c>VK_QUEUE_FAMILY_IGNORED</c>.</summary>
    public uint DstQueueFamilyIndex;
    /// <summary>The native <c>VkBuffer</c> handle whose backing memory the barrier covers.</summary>
    public nint Buffer;
    /// <summary>The offset, in bytes, of the covered range into the buffer.</summary>
    public ulong Offset;
    /// <summary>The size, in bytes, of the covered range, or <c>VK_WHOLE_SIZE</c> for the rest of the buffer.</summary>
    public ulong Size;
}
