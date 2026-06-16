using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the vertex input state of a graphics pipeline: the vertex buffer bindings and the attributes
/// read from them.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineVertexInputStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineVertexInputStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PVertexBindingDescriptions"/> array.</summary>
    public uint VertexBindingDescriptionCount;
    /// <summary>A pointer to an array of <c>VkVertexInputBindingDescription</c> structures describing the vertex buffer bindings.</summary>
    public nint PVertexBindingDescriptions;
    /// <summary>The number of entries in the <see cref="PVertexAttributeDescriptions"/> array.</summary>
    public uint VertexAttributeDescriptionCount;
    /// <summary>A pointer to an array of <c>VkVertexInputAttributeDescription</c> structures describing the vertex attributes.</summary>
    public nint PVertexAttributeDescriptions;
}
