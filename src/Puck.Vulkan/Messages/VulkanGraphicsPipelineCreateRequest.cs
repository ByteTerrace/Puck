using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a graphics pipeline to create: the shaders and render pass it targets, the fixed-function
/// state, the vertex input layout, the descriptor and push constant layout, and the dynamic state.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
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
public readonly record struct VulkanGraphicsPipelineCreateRequest(
    nint DeviceHandle,
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
    VkPipelineMultisampleStateCreateInfo Multisample
);
