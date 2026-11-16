using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the process's budget and usage of each memory heap (<c>VK_EXT_memory_budget</c>), chained to
/// <see cref="VkPhysicalDeviceMemoryProperties2"/>. Entries at or past the heap count are zero.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceMemoryBudgetPropertiesEXT {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value
    /// (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_EXT</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or zero.</summary>
    public nint PNext;
    /// <summary>Each heap's budget, in bytes: how much the process can use before allocations may fail or degrade,
    /// one entry per <c>VK_MAX_MEMORY_HEAPS</c>.</summary>
    public fixed ulong HeapBudget[16];
    /// <summary>Each heap's usage, in bytes: an estimate of how much the process has allocated from it.</summary>
    public fixed ulong HeapUsage[16];
}
