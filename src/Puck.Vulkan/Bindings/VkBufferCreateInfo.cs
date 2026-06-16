using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a buffer to be created with <c>vkCreateBuffer</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkBufferCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkBufferCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkBufferCreateFlagBits</c> specifying additional parameters of the buffer.</summary>
    public uint Flags;
    /// <summary>The size, in bytes, of the buffer to be created.</summary>
    public ulong Size;
    /// <summary>A bitmask of <c>VkBufferUsageFlagBits</c> specifying the allowed usages of the buffer.</summary>
    public uint Usage;
    /// <summary>The sharing mode used when the buffer is accessed by multiple queue families, as a <c>VkSharingMode</c> value.</summary>
    public uint SharingMode;
    /// <summary>The number of entries in the <see cref="PQueueFamilyIndices"/> array. Used only when <see cref="SharingMode"/> is concurrent.</summary>
    public uint QueueFamilyIndexCount;
    /// <summary>A pointer to an array of queue family indices that will access the buffer. Used only when <see cref="SharingMode"/> is concurrent.</summary>
    public nint PQueueFamilyIndices;
}
