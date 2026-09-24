using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a graphics pipeline to create: the shaders and render pass it targets, the fixed-function
/// state, the vertex input layout, the descriptor and push constant layout, and the dynamic state.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle the pipeline is used with.</param>
/// <param name="VertexShaderModuleHandle">The native <c>VkShaderModule</c> handle of the vertex shader.</param>
/// <param name="FragmentShaderModuleHandle">The native <c>VkShaderModule</c> handle of the fragment shader.</param>
/// <param name="Width">The viewport and scissor width, in pixels.</param>
/// <param name="Height">The viewport and scissor height, in pixels.</param>
/// <param name="PushConstantSize">The size, in bytes, of the push constant range, or zero for none.</param>
/// <param name="PushConstantStageFlags">A bitmask of <c>VkShaderStageFlagBits</c> identifying the stages that access the push constants.</param>
/// <param name="Topology">The primitive topology, as a <c>VkPrimitiveTopology</c> value.</param>
/// <param name="VertexBindings">The vertex buffer binding descriptions.</param>
/// <param name="VertexAttributes">The vertex attribute descriptions.</param>
/// <param name="DescriptorBindings">The descriptor set layout bindings.</param>
/// <param name="ColorBlendAttachments">The per-attachment color blend states.</param>
/// <param name="DynamicStates">The pieces of pipeline state set dynamically, as <c>VkDynamicState</c> values.</param>
/// <param name="Rasterization">The rasterization state.</param>
/// <param name="Multisample">The multisample state.</param>
/// <param name="PipelineCache">The device's pipeline cache, passed to the driver and counting the creation, or
/// <see langword="null"/> for a device created without one.</param>
/// <param name="ClipSpaceYUp">Whether the viewport has negative height, so clip-space +y is the top of the attachment.</param>
/// <param name="DepthStencil">The depth state of a pipeline whose render pass has a depth attachment, or
/// <see langword="null"/> for one without.</param>
public readonly record struct VulkanGraphicsPipelineCreateRequest(
    VulkanDeviceCommands Device,
    nint RenderPassHandle,
    nint VertexShaderModuleHandle,
    nint FragmentShaderModuleHandle,
    uint Width,
    uint Height,
    uint PushConstantSize,
    uint PushConstantStageFlags,
    uint Topology,
    IReadOnlyList<VkVertexInputBindingDescription> VertexBindings,
    IReadOnlyList<VkVertexInputAttributeDescription> VertexAttributes,
    IReadOnlyList<VkDescriptorSetLayoutBinding> DescriptorBindings,
    IReadOnlyList<VkPipelineColorBlendAttachmentState> ColorBlendAttachments,
    IReadOnlyList<uint> DynamicStates,
    VkPipelineRasterizationStateCreateInfo Rasterization,
    VkPipelineMultisampleStateCreateInfo Multisample,
    VulkanPipelineCache? PipelineCache,
    VkPipelineDepthStencilStateCreateInfo? DepthStencil = null,
    bool ClipSpaceYUp = false
);
