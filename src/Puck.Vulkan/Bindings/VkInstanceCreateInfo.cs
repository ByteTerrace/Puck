using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a Vulkan instance to be created with <c>vkCreateInstance</c>, including the layers
/// and extensions to enable.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkInstanceCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkInstanceCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO</c>).</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A bitmask of <c>VkInstanceCreateFlagBits</c> specifying behavior of the instance.</summary>
    public uint Flags;
    /// <summary>A pointer to a <c>VkApplicationInfo</c> structure identifying the application, or <see langword="null"/>.</summary>
    public nint ApplicationInfo;
    /// <summary>The number of global layers to enable.</summary>
    public uint EnabledLayerCount;
    /// <summary>A pointer to an array of null-terminated UTF-8 strings naming the layers to enable.</summary>
    public nint EnabledLayerNames;
    /// <summary>The number of global extensions to enable.</summary>
    public uint EnabledExtensionCount;
    /// <summary>A pointer to an array of null-terminated UTF-8 strings naming the extensions to enable.</summary>
    public nint EnabledExtensionNames;
}
