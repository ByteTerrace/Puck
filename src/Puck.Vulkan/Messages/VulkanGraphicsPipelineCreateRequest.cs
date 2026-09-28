using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a graphics pipeline to create: the shaders and render pass it targets, the pipeline layout it is created
/// with, the fixed-function state, the vertex input layout and the dynamic state.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle the pipeline is used with.</param>
/// <param name="VertexShaderModuleHandle">The native <c>VkShaderModule</c> handle of the vertex shader.</param>
/// <param name="FragmentShaderModuleHandle">The native <c>VkShaderModule</c> handle of the fragment shader.</param>
/// <param name="PipelineLayoutHandle">The native <c>VkPipelineLayout</c> the pipeline is created with, which the caller
/// created from the pipeline's groups and owns.</param>
/// <param name="Topology">The primitive topology, as a <c>VkPrimitiveTopology</c> value.</param>
/// <param name="VertexBindings">The vertex buffer binding descriptions.</param>
/// <param name="VertexAttributes">The vertex attribute descriptions.</param>
/// <param name="ColorBlendAttachments">The per-attachment color blend states.</param>
/// <param name="DynamicStates">The pieces of pipeline state set dynamically, as <c>VkDynamicState</c> values; they name
/// the viewport and the scissor, which the command buffer drawing with the pipeline sets.</param>
/// <param name="Rasterization">The rasterization state.</param>
/// <param name="Multisample">The multisample state.</param>
/// <param name="PipelineCache">The device's pipeline cache, passed to the driver and counting the creation, or
/// <see langword="null"/> for a device created without one.</param>
/// <param name="DepthStencil">The depth state of a pipeline whose render pass has a depth attachment, or
/// <see langword="null"/> for one without.</param>
public readonly record struct VulkanGraphicsPipelineCreateRequest(
    VulkanDeviceCommands Device,
    nint RenderPassHandle,
    nint VertexShaderModuleHandle,
    nint FragmentShaderModuleHandle,
    nint PipelineLayoutHandle,
    uint Topology,
    IReadOnlyList<VkVertexInputBindingDescription> VertexBindings,
    IReadOnlyList<VkVertexInputAttributeDescription> VertexAttributes,
    IReadOnlyList<VkPipelineColorBlendAttachmentState> ColorBlendAttachments,
    IReadOnlyList<uint> DynamicStates,
    VkPipelineRasterizationStateCreateInfo Rasterization,
    VkPipelineMultisampleStateCreateInfo Multisample,
    VulkanPipelineCache? PipelineCache,
    VkPipelineDepthStencilStateCreateInfo? DepthStencil = null
);
