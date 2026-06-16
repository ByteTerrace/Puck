using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single memory type of a physical device — its properties and the heap it allocates from.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryType (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryType {
    /// <summary>A bitmask of <c>VkMemoryPropertyFlagBits</c> describing the properties of this memory type.</summary>
    public uint PropertyFlags;
    /// <summary>The index of the memory heap from which this memory type allocates.</summary>
    public uint HeapIndex;
}
