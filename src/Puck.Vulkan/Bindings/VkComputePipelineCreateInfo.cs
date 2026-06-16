using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a compute pipeline to be created with <c>vkCreateComputePipelines</c>: its single
/// compute shader stage and pipeline layout.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkComputePipelineCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkComputePipelineCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkPipelineCreateFlagBits</c> specifying how the pipeline is created.</summary>
    public uint Flags;
    /// <summary>The compute shader stage of the pipeline.</summary>
    public VkPipelineShaderStageCreateInfo Stage;
    /// <summary>The pipeline layout the pipeline uses (a <c>VkPipelineLayout</c> handle).</summary>
    public nint Layout;
    /// <summary>A pipeline to derive from (a <c>VkPipeline</c> handle), or <see langword="null"/>.</summary>
    public nint BasePipelineHandle;
    /// <summary>The index, within the array of create-infos passed to the create call, of a pipeline to derive from, or <c>-1</c>.</summary>
    public int BasePipelineIndex;
}
