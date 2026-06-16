using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters supplied to <c>vkBeginCommandBuffer</c> when recording into a command buffer begins.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkCommandBufferBeginInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkCommandBufferBeginInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkCommandBufferUsageFlagBits</c> specifying how the command buffer will be used.</summary>
    public uint Flags;
    /// <summary>A pointer to a <c>VkCommandBufferInheritanceInfo</c> structure for a secondary command buffer; ignored for primary command buffers.</summary>
    public nint PInheritanceInfo;
}
