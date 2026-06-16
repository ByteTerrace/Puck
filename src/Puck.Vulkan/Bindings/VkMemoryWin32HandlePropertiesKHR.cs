using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Receives the memory types compatible with a given external Win32 handle, from
/// <c>vkGetMemoryWin32HandlePropertiesKHR</c>; the importing allocation's memory type must be one of them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryWin32HandlePropertiesKHR {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of memory type indices (bit <c>i</c> = memory type <c>i</c>) compatible with the handle.</summary>
    public uint MemoryTypeBits;
}
