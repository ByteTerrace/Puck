using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained onto a <c>VkMemoryAllocateInfo</c> to bind the allocation to a single image or buffer — required
/// when importing an external resource (such as a Direct3D 12 texture), which is always a dedicated allocation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryDedicatedAllocateInfo {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The native <c>VkImage</c> the allocation is dedicated to, or zero.</summary>
    public nint Image;
    /// <summary>The native <c>VkBuffer</c> the allocation is dedicated to, or zero.</summary>
    public nint Buffer;
}
