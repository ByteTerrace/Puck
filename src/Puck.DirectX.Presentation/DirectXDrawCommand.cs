namespace Puck.DirectX.Presentation;

/// <summary>
/// One recorded draw: the pipeline to bind (a <c>GCHandle</c>-as-<see cref="nint"/> token to a
/// <c>DirectXPipelineLayout</c>, the pipeline's <c>LayoutHandle</c>), the group whose tables it binds, the shader-visible
/// view and sampler heaps those tables live in with each table's GPU handle, the instanced draw parameters, and a
/// sequence key that preserves painter's order across pipelines. Zero values for the optional fields mean "no change /
/// not used". The recorder binds the view table at the group's <c>DirectXGroupLayout.ViewTableIndex</c> and the sampler
/// table at its <c>SamplerTableIndex</c>.
/// </summary>
public readonly record struct DirectXDrawCommand(
    DirectXDrawParameters DrawParameters,
    nint PipelineLayoutHandle = 0,
    uint Group = 0,
    nint ViewHeapHandle = 0,
    ulong ViewTableGpuHandle = 0,
    nint SamplerHeapHandle = 0,
    ulong SamplerTableGpuHandle = 0,
    long SequenceKey = 0
);
