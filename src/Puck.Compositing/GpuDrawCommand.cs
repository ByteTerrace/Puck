using Puck.Abstractions;
using Puck.Assets;

namespace Puck.Compositing;

/// <summary>
/// One recorded draw in a content-addressed compositing list: the pipeline to bind — identified by the content
/// hash of its defining shader asset (<see cref="PipelineId"/>), resolved against the pipeline map passed to
/// <see cref="GpuCompositor"/> — together with the per-draw descriptor set and vertex buffer, optional push
/// constants, and the instanced draw parameters. The pipeline identity is the same content hash on every
/// backend, so a draw list is portable across backends unchanged. Commands are replayed in list order;
/// <see cref="SequenceKey"/> is the caller's sort key for establishing painter's order. A zero handle on an
/// optional field means "leave the currently bound state unchanged".
/// </summary>
public readonly record struct GpuDrawCommand(
    GpuDrawParameters DrawParameters,
    AssetContentHash PipelineId = default,
    nint DescriptorSetHandle = 0,
    nint VertexBufferHandle = 0,
    GpuPushConstantBinding? PushConstants = null,
    long SequenceKey = 0
);
