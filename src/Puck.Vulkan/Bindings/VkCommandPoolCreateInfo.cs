using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a command pool to be created with <c>vkCreateCommandPool</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkCommandPoolCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkCommandPoolCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkCommandPoolCreateFlagBits</c> specifying the usage behavior of the pool and the command buffers allocated from it.</summary>
    public uint Flags;
    /// <summary>The queue family that all command buffers allocated from this pool are submitted to.</summary>
    public uint QueueFamilyIndex;
}
