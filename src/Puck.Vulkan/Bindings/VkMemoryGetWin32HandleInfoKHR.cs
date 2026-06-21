using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies the device memory and handle type to retrieve a Win32 NT handle for, via <c>vkGetMemoryWin32HandleKHR</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryGetWin32HandleInfoKHR {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_MEMORY_GET_WIN32_HANDLE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The native <c>VkDeviceMemory</c> handle to export.</summary>
    public nint Memory;
    /// <summary>The <c>VkExternalMemoryHandleTypeFlagBits</c> value describing the handle to retrieve.</summary>
    public uint HandleType;
}
