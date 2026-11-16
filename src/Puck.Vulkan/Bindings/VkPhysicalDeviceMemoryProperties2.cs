using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports a physical device's memory types and heaps, extended through a <c>pNext</c> chain
/// (<c>vkGetPhysicalDeviceMemoryProperties2</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkPhysicalDeviceMemoryProperties2 {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value
    /// (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_PROPERTIES_2</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the chained structure that receives extended memory properties, or zero.</summary>
    public nint PNext;
    /// <summary>The memory types and heaps.</summary>
    public VkPhysicalDeviceMemoryProperties MemoryProperties;
}
