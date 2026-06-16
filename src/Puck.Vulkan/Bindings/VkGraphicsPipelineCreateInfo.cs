using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a graphics pipeline to be created with <c>vkCreateGraphicsPipelines</c>: its shader
/// stages, fixed-function state, layout, and the render pass / subpass it targets.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkGraphicsPipelineCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkGraphicsPipelineCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkPipelineCreateFlagBits</c> specifying how the pipeline is created.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PStages"/> array.</summary>
    public uint StageCount;
    /// <summary>A pointer to an array of <c>VkPipelineShaderStageCreateInfo</c> structures describing the shader stages.</summary>
    public nint PStages;
    /// <summary>A pointer to a <c>VkPipelineVertexInputStateCreateInfo</c> structure, or <see langword="null"/> when the state is dynamic.</summary>
    public nint PVertexInputState;
    /// <summary>A pointer to a <c>VkPipelineInputAssemblyStateCreateInfo</c> structure, or <see langword="null"/> when not required.</summary>
    public nint PInputAssemblyState;
    /// <summary>A pointer to a <c>VkPipelineTessellationStateCreateInfo</c> structure, or <see langword="null"/> when tessellation is not used.</summary>
    public nint PTessellationState;
    /// <summary>A pointer to a <c>VkPipelineViewportStateCreateInfo</c> structure, or <see langword="null"/> when rasterization is disabled.</summary>
    public nint PViewportState;
    /// <summary>A pointer to a <c>VkPipelineRasterizationStateCreateInfo</c> structure.</summary>
    public nint PRasterizationState;
    /// <summary>A pointer to a <c>VkPipelineMultisampleStateCreateInfo</c> structure, or <see langword="null"/> when rasterization is disabled.</summary>
    public nint PMultisampleState;
    /// <summary>A pointer to a <c>VkPipelineDepthStencilStateCreateInfo</c> structure, or <see langword="null"/> when the subpass uses no depth/stencil attachment.</summary>
    public nint PDepthStencilState;
    /// <summary>A pointer to a <c>VkPipelineColorBlendStateCreateInfo</c> structure, or <see langword="null"/> when the subpass uses no color attachment.</summary>
    public nint PColorBlendState;
    /// <summary>A pointer to a <c>VkPipelineDynamicStateCreateInfo</c> structure, or <see langword="null"/> when no state is dynamic.</summary>
    public nint PDynamicState;
    /// <summary>The pipeline layout the pipeline uses (a <c>VkPipelineLayout</c> handle).</summary>
    public nint Layout;
    /// <summary>The render pass the pipeline is used with (a <c>VkRenderPass</c> handle).</summary>
    public nint RenderPass;
    /// <summary>The index of the subpass in <see cref="RenderPass"/> in which the pipeline is used.</summary>
    public uint Subpass;
    /// <summary>A pipeline to derive from (a <c>VkPipeline</c> handle), or <see langword="null"/>.</summary>
    public nint BasePipelineHandle;
    /// <summary>The index, within the array of create-infos passed to the create call, of a pipeline to derive from, or <c>-1</c>.</summary>
    public int BasePipelineIndex;
}
