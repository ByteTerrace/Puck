using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a pipeline layout to be created with <c>vkCreatePipelineLayout</c>: the descriptor
/// set layouts and push constant ranges accessible to a pipeline.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineLayoutCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineLayoutCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkPipelineLayoutCreateFlagBits</c> specifying additional parameters of the layout.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PSetLayouts"/> array.</summary>
    public uint SetLayoutCount;
    /// <summary>A pointer to an array of <c>VkDescriptorSetLayout</c> handles, one per descriptor set the pipeline uses.</summary>
    public nint PSetLayouts;
    /// <summary>The number of entries in the <see cref="PPushConstantRanges"/> array.</summary>
    public uint PushConstantRangeCount;
    /// <summary>A pointer to an array of <c>VkPushConstantRange</c> structures describing the push constant ranges the pipeline uses.</summary>
    public nint PPushConstantRanges;
}
