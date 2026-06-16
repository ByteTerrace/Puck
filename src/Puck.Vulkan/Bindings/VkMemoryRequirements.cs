using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the memory requirements of a resource: the allocation size and alignment it needs, and which
/// memory types are compatible with it.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryRequirements (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryRequirements {
    /// <summary>The size, in bytes, of the memory allocation required for the resource.</summary>
    public ulong Size;
    /// <summary>The alignment, in bytes, required for the offset of the resource within its allocation.</summary>
    public ulong Alignment;
    /// <summary>A bitmask in which a set bit at index <c>i</c> indicates that memory type <c>i</c> is supported for the resource.</summary>
    public uint MemoryTypeBits;
}
