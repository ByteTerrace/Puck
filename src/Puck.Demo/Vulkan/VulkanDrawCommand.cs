using Puck.Assets;

namespace Puck.Vulkan;

/// <summary>One recorded draw: the pipeline to bind (by content id), its per-draw bindings, and the
/// instanced draw parameters. <see cref="SequenceKey"/> preserves painter's order across pipelines.</summary>
public readonly record struct VulkanDrawCommand(
    VulkanDrawParameters DrawParameters,
    VulkanPushConstantBinding? PushConstantBinding = null,
    nint DescriptorSetHandle = 0,
    VulkanVertexBufferBinding? VertexBufferBinding = null,
    AssetContentHash PipelineId = default,
    long SequenceKey = 0
);
