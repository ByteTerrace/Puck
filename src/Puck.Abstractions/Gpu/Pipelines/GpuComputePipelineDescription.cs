namespace Puck.Abstractions.Gpu;

/// <summary>Describes a compute pipeline: its descriptor set 0, an optional push-constant range, the static-sampler
/// filter a <see cref="GpuComputeBindingKind.SampledImage"/> binding uses on Direct3D 12, and how Direct3D 12 numbers
/// the kernel's registers; or, instead of all four, the frequency groups it binds (<see cref="Layout"/>).</summary>
/// <param name="Name">A diagnostics-only label; not read by either backend factory.</param>
/// <param name="Bindings">The descriptor bindings of set 0, in binding order; empty when <see cref="Layout"/> states
/// the pipeline's bindings.</param>
/// <param name="PushConstantBinding">The push-constant range, or <see langword="null"/> when the pipeline has none or
/// <see cref="Layout"/> states its pushed index.</param>
/// <param name="SamplerFilter">The static-sampler filter for a <see cref="GpuComputeBindingKind.SampledImage"/>
/// binding. Direct3D 12 only; Vulkan's sampler is a bound descriptor, so this is ignored there, and so is a pipeline
/// created from a <see cref="Layout"/>, whose samplers are descriptors on both backends.</param>
/// <param name="Registers">How Direct3D 12 numbers each binding's register: at its binding number unless the kernel
/// declares <see cref="GpuRegisterNumbering.PackedByClass"/> registers. Vulkan ignores it, and a pipeline created from
/// a <see cref="Layout"/> takes its registers at its binding numbers.</param>
/// <param name="Layout">The frequency groups the pipeline binds and whether it pushes an index, from which each backend
/// plans its root signature or pipeline layout (<c>DirectXRootLayout.Plan</c>, <c>VulkanGroupLayouts.Plan</c>), with its
/// samplers in sampler tables; <see langword="null"/> for a pipeline whose bindings are set 0's
/// <see cref="Bindings"/>.</param>
public sealed record GpuComputePipelineDescription(
    string Name,
    IReadOnlyList<GpuComputeBinding> Bindings,
    GpuPushConstantBinding? PushConstantBinding,
    GpuSamplerFilter SamplerFilter = GpuSamplerFilter.Linear,
    GpuRegisterNumbering Registers = GpuRegisterNumbering.Binding,
    GpuPipelineLayoutDescription? Layout = null
) {
    /// <summary>Returns the groups a pipeline created from <see cref="Layout"/> binds, refusing a description that
    /// states its bindings twice or a layout that is not a compute pipeline's.</summary>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="Layout"/> is set beside <see cref="Bindings"/>, a
    /// <see cref="PushConstantBinding"/> or <see cref="GpuRegisterNumbering.PackedByClass"/> registers, or its stages
    /// are not <see cref="GpuShaderStage.Compute"/>.</exception>
    public GpuPipelineLayoutDescription RequireLayout() {
        var layout = (Layout ?? throw new InvalidOperationException(message: $"Compute pipeline '{Name}' states no layout."));

        if (
            (Bindings.Count != 0) ||
            (PushConstantBinding is not null) ||
            (Registers != GpuRegisterNumbering.Binding)
        ) {
            throw new ArgumentException(
                message: $"Compute pipeline '{Name}' states its bindings twice: a layout's groups beside set 0's bindings, a push-constant range or packed registers.",
                paramName: nameof(Layout)
            );
        }
        if (layout.Stages != GpuShaderStage.Compute) {
            throw new ArgumentException(
                message: $"Compute pipeline '{Name}' has a layout for the stages '{layout.Stages}', not compute.",
                paramName: nameof(Layout)
            );
        }

        return layout;
    }
}
