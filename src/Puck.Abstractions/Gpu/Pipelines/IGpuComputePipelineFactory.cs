namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral compute pipelines from a compiled compute shader, an ordered set of descriptor bindings
/// (set 0), and an optional push-constant range.
/// </summary>
public interface IGpuComputePipelineFactory {
    /// <summary>Creates a compute pipeline.</summary>
    /// <param name="deviceContext">The device to create the pipeline on.</param>
    /// <param name="computeShaderModule">The compiled compute shader module.</param>
    /// <param name="bindings">The descriptor bindings of set 0, in binding order.</param>
    /// <param name="pushConstantBinding">The push-constant range, or <see langword="null"/> when the pipeline has none.</param>
    /// <param name="samplerFilter">The filter of the static sampler baked into the root signature when a binding is a
    /// <see cref="GpuComputeBindingKind.SampledImage"/>. Direct3D 12 only — Vulkan's sampler is a bound descriptor, so
    /// this is ignored there. Has no effect when no binding samples an image.</param>
    /// <returns>The created compute pipeline.</returns>
    IGpuComputePipeline Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, IReadOnlyList<GpuComputeBinding> bindings, GpuPushConstantBinding? pushConstantBinding, GpuSamplerFilter samplerFilter = GpuSamplerFilter.Linear);
}
