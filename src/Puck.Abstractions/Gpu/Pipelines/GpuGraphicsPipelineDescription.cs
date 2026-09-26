namespace Puck.Abstractions.Gpu;

/// <summary>
/// Describes a graphics pipeline both backends build identically: a vertex input layout, the frequency groups it binds
/// (<see cref="Layout"/>), and the depth test of a render pass with a depth attachment. Every such pipeline draws an
/// opaque, single-sampled, unculled triangle list whose clip-space +y is the top of the attachment: a fragment replaces
/// what its color attachment holds. A field only one backend would honor is a defect, not a capability.
/// </summary>
/// <param name="Name">A diagnostics-only label; not read by either backend factory.</param>
/// <param name="VertexInput">The pipeline's vertex input layout.</param>
/// <param name="Layout">The frequency groups the pipeline binds and whether it pushes an index, from which each backend
/// plans its root signature or pipeline layout (<c>DirectXRootLayout.Plan</c>, <c>VulkanGroupLayouts.Plan</c>), with its
/// samplers in sampler tables.</param>
/// <param name="DepthCompare">The depth test a passing fragment then writes its depth through, which the render pass's
/// depth attachment requires; <see langword="null"/> for a render pass without one.</param>
public sealed record GpuGraphicsPipelineDescription(
    string Name,
    GpuVertexInputLayout VertexInput,
    GpuPipelineLayoutDescription Layout,
    GpuDepthCompare? DepthCompare = null
) {
    /// <summary>Returns the groups the pipeline binds, refusing a layout that is not a graphics pipeline's.</summary>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="Layout"/>'s stages are <see cref="GpuShaderStage.Compute"/>.</exception>
    public GpuPipelineLayoutDescription RequireLayout() {
        ArgumentNullException.ThrowIfNull(argument: Layout);

        if (Layout.Stages == GpuShaderStage.Compute) {
            throw new ArgumentException(
                message: $"Graphics pipeline '{Name}' has a layout for the compute stage.",
                paramName: nameof(Layout)
            );
        }

        return Layout;
    }
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
