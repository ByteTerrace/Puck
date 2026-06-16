using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a Vulkan surface for a Win32 window, created with <c>vkCreateWin32SurfaceKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkWin32SurfaceCreateInfoKHR (vulkan_win32.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkWin32SurfaceCreateInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The Win32 <c>HINSTANCE</c> of the module owning the window.</summary>
    public nint InstanceHandle;
    /// <summary>The Win32 <c>HWND</c> of the window the surface is associated with.</summary>
    public nint WindowHandle;
}
