using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A global memory barrier used in a pipeline barrier: it scopes a memory dependency across all memory
/// accesses of the given types, rather than for a specific resource.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryBarrier (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryBarrier {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_MEMORY_BARRIER</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the source access scope of the barrier.</summary>
    public uint SrcAccessMask;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope of the barrier.</summary>
    public uint DstAccessMask;
}
