using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a Vulkan surface for an X11 window via XCB, created with <c>vkCreateXcbSurfaceKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkXcbSurfaceCreateInfoKHR (vulkan_xcb.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names. <see cref="Connection"/> is an <c>xcb_connection_t*</c>; <see cref="Window"/> is an <c>xcb_window_t</c>
/// (a 32-bit XID).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkXcbSurfaceCreateInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_XCB_SURFACE_CREATE_INFO_KHR</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The XCB connection to the X server (an <c>xcb_connection_t*</c>).</summary>
    public nint Connection;
    /// <summary>The XCB window the surface is associated with (an <c>xcb_window_t</c> XID).</summary>
    public uint Window;
}
