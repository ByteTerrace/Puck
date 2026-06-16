using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a fence to be created with <c>vkCreateFence</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkFenceCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkFenceCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_FENCE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkFenceCreateFlagBits</c>; set <c>VK_FENCE_CREATE_SIGNALED_BIT</c> to create the fence already signaled.</summary>
    public uint Flags;
}
