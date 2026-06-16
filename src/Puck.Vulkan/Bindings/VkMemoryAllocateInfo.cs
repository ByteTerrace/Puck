using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a device memory allocation requested with <c>vkAllocateMemory</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryAllocateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryAllocateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>. <c>VkMemoryAllocateFlagsInfo</c> is chained here.</summary>
    public nint PNext;
    /// <summary>The size, in bytes, of the allocation.</summary>
    public ulong AllocationSize;
    /// <summary>The index of the memory type (in <c>VkPhysicalDeviceMemoryProperties.memoryTypes</c>) to allocate from.</summary>
    public uint MemoryTypeIndex;
}
