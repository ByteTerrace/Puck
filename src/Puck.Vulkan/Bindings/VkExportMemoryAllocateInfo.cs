using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained onto a <c>VkMemoryAllocateInfo</c> to declare that the allocated memory may be exported to an external
/// handle of the given types (for example an opaque Win32 NT handle another Vulkan instance imports).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkExportMemoryAllocateInfo {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkExternalMemoryHandleTypeFlagBits</c> the memory may be exported to.</summary>
    public uint HandleTypes;
}
