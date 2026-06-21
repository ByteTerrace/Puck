using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained onto a <c>VkExportMemoryAllocateInfo</c> to specify the Win32 security attributes and access rights of an
/// exported NT handle. Required by the spec when the exported handle types include an NT handle (for example opaque
/// Win32) on Windows.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkExportMemoryWin32HandleInfoKHR {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_EXPORT_MEMORY_WIN32_HANDLE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A pointer to a <c>SECURITY_ATTRIBUTES</c> for the exported handle, or <see langword="null"/> for the default.</summary>
    public nint PAttributes;
    /// <summary>A bitmask of access rights (a Win32 <c>DWORD</c>) for the exported handle, for example <c>GENERIC_ALL</c>.</summary>
    public uint DwAccess;
    /// <summary>An optional name for a named handle (an <c>LPCWSTR</c>), or <see langword="null"/>.</summary>
    public nint Name;
}
