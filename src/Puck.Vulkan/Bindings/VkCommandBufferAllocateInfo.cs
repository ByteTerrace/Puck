using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing the command buffers to allocate with <c>vkAllocateCommandBuffers</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkCommandBufferAllocateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkCommandBufferAllocateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The command pool from which the buffers are allocated (a <c>VkCommandPool</c> handle).</summary>
    public nint CommandPool;
    /// <summary>The level of the allocated buffers (primary or secondary), as a <c>VkCommandBufferLevel</c> value.</summary>
    public uint Level;
    /// <summary>The number of command buffers to allocate.</summary>
    public uint CommandBufferCount;
}
