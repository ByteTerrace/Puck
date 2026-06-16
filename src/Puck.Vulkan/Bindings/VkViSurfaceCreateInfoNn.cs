using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a Vulkan surface for an <c>nn::vi</c> layer, created with <c>vkCreateViSurfaceNN</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkViSurfaceCreateInfoNN (vulkan_vi.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// <see cref="Window"/> is the <c>void*</c> nn::vi native window handle (an <c>nn::vi::NativeWindowHandle</c>).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkViSurfaceCreateInfoNn {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_VI_SURFACE_CREATE_INFO_NN</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The <c>nn::vi</c> native window handle the surface is associated with (a <c>void*</c>).</summary>
    public nint Window;
}
