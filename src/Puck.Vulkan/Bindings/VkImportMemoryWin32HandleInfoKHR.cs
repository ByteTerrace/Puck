using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained onto a <c>VkMemoryAllocateInfo</c> to import device memory from a Win32 NT handle (for example a
/// shared Direct3D 12 resource handle) rather than allocating fresh memory.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkImportMemoryWin32HandleInfoKHR {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The <c>VkExternalMemoryHandleTypeFlagBits</c> value describing <see cref="Handle"/>.</summary>
    public uint HandleType;
    /// <summary>The external NT handle to import.</summary>
    public nint Handle;
    /// <summary>An optional name for a named handle, or <see langword="null"/>.</summary>
    public nint Name;
}
