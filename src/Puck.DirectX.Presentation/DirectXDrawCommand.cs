using Puck.Abstractions;

namespace Puck.DirectX.Presentation;

/// <summary>
/// One recorded draw: the pipeline to bind (a <c>GCHandle</c>-as-<see cref="nint"/> token to a
/// <c>DirectXPipelineLayout</c>), the descriptor heap and GPU table handle, an optional vertex-buffer token,
/// optional root 32-bit constants, the instanced draw parameters, and a sequence key that preserves
/// painter's order across pipelines. Zero values for the optional fields mean "no change / not used".
/// </summary>
public readonly record struct DirectXDrawCommand(
    DirectXDrawParameters DrawParameters,
    nint PipelineLayoutHandle = 0,
    nint DescriptorHeapHandle = 0,
    ulong DescriptorTableGpuHandle = 0,
    nint VertexBufferHandle = 0,
    GpuPushConstantBinding? RootConstants = null,
    long SequenceKey = 0
);
