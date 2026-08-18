namespace Puck.Abstractions.Gpu;

/// <summary>
/// Describes a graphics pipeline both backends can build identically: a vertex input layout, a texture-sampler
/// count, an optional storage buffer, and an optional push-constant range. Carries no raster, blend, depth,
/// topology, or render-target-format field — neither backend factory accepts a caller-supplied value for those
/// today, and a field only one backend would honor is a defect, not a capability.
/// </summary>
/// <param name="Name">A diagnostics-only label; not read by either backend factory.</param>
/// <param name="VertexInput">The pipeline's vertex input layout.</param>
/// <param name="TextureSamplerCount">The number of combined image-sampler descriptors.</param>
/// <param name="EnableStorageBuffer">Whether to include a storage buffer binding.</param>
/// <param name="PushConstantBinding">The push constant range, or <see langword="null"/> for none.</param>
public sealed record GpuGraphicsPipelineDescription(
    string Name,
    GpuVertexInputLayout VertexInput,
    uint TextureSamplerCount,
    bool EnableStorageBuffer,
    GpuPushConstantBinding? PushConstantBinding
);
