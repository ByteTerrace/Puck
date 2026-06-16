using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single memory heap of a physical device, from which memory types allocate.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkMemoryHeap (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryHeap {
    /// <summary>The total size, in bytes, of the heap.</summary>
    public ulong Size;
    /// <summary>A bitmask of <c>VkMemoryHeapFlagBits</c> describing attributes of the heap.</summary>
    public uint Flags;
}
