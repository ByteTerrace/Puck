namespace Puck.Abstractions.Gpu;

/// <summary>Describes a compute pipeline: its descriptor set 0, an optional push-constant range, and the
/// static-sampler filter a <see cref="GpuComputeBindingKind.SampledImage"/> binding uses on Direct3D 12.</summary>
/// <param name="Name">A diagnostics-only label; not read by either backend factory.</param>
/// <param name="Bindings">The descriptor bindings of set 0, in binding order.</param>
/// <param name="PushConstantBinding">The push-constant range, or <see langword="null"/> when the pipeline has none.</param>
/// <param name="SamplerFilter">The static-sampler filter for a <see cref="GpuComputeBindingKind.SampledImage"/>
/// binding. Direct3D 12 only; Vulkan's sampler is a bound descriptor, so this is ignored there.</param>
public sealed record GpuComputePipelineDescription(
    string Name,
    IReadOnlyList<GpuComputeBinding> Bindings,
    GpuPushConstantBinding? PushConstantBinding,
    GpuSamplerFilter SamplerFilter = GpuSamplerFilter.Linear
);
