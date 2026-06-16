using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a Vulkan surface for a Wayland window, created with <c>vkCreateWaylandSurfaceKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkWaylandSurfaceCreateInfoKHR (vulkan_wayland.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names. <see cref="Display"/> is a <c>struct wl_display*</c> and <see cref="Surface"/> a <c>struct wl_surface*</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkWaylandSurfaceCreateInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The Wayland display connection (a <c>struct wl_display*</c>).</summary>
    public nint Display;
    /// <summary>The Wayland surface the Vulkan surface is associated with (a <c>struct wl_surface*</c>).</summary>
    public nint Surface;
}
