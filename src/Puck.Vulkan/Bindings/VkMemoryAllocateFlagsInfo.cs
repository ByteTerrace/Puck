using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Extends <c>VkMemoryAllocateInfo</c> (via its <c>pNext</c> chain) with allocation flags, including the
/// flag required to allocate memory usable for buffer device addresses.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryAllocateFlagsInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryAllocateFlagsInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_FLAGS_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkMemoryAllocateFlagBits</c> controlling the allocation.</summary>
    public uint Flags;
    /// <summary>A mask of physical devices in a device group on which the memory is allocated; used only when device-mask allocation is requested in <see cref="Flags"/>.</summary>
    public uint DeviceMask;
}
