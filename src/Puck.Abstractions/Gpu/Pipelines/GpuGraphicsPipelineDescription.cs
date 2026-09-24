namespace Puck.Abstractions.Gpu;

/// <summary>
/// Describes a graphics pipeline both backends build identically: a vertex input layout, a texture-sampler count, an
/// optional storage buffer, an optional push-constant range, and the depth test of a render pass with a depth
/// attachment. Every such pipeline draws an opaque, single-sampled, unculled triangle list whose clip-space +y is the
/// top of the attachment: a fragment replaces what its color attachment holds. A field only one backend would honor is
/// a defect, not a capability.
/// </summary>
/// <param name="Name">A diagnostics-only label; not read by either backend factory.</param>
/// <param name="VertexInput">The pipeline's vertex input layout.</param>
/// <param name="TextureSamplerCount">The number of combined image-sampler descriptors.</param>
/// <param name="EnableStorageBuffer">Whether to include a storage buffer binding.</param>
/// <param name="PushConstantBinding">The push constant range, or <see langword="null"/> for none.</param>
/// <param name="DepthCompare">The depth test a passing fragment then writes its depth through, which the render pass's
/// depth attachment requires; <see langword="null"/> for a render pass without one.</param>
public sealed record GpuGraphicsPipelineDescription(
    string Name,
    GpuVertexInputLayout VertexInput,
    uint TextureSamplerCount,
    bool EnableStorageBuffer,
    GpuPushConstantBinding? PushConstantBinding,
    GpuDepthCompare? DepthCompare = null
) {
    /// <summary>Refuses a description whose depth test disagrees with the render pass it is created for: a depth
    /// attachment needs a depth test, and a depth test needs a depth attachment.</summary>
    /// <param name="renderPass">The render pass the pipeline draws in.</param>
    /// <exception cref="ArgumentException">The render pass has a depth attachment and the description no depth test, or
    /// the reverse.</exception>
    public void ValidateAgainst(IGpuRenderPass renderPass) {
        ArgumentNullException.ThrowIfNull(renderPass);

        if ((renderPass.Description.Depth is null) != (DepthCompare is null)) {
            throw new ArgumentException(
                message: $"Graphics pipeline '{Name}' declares a depth test exactly when its render pass has a depth attachment.",
                paramName: nameof(renderPass)
            );
        }
    }
}
