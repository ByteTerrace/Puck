namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral compute pipelines from a compiled compute shader, an ordered set of descriptor bindings
/// (set 0), and an optional push-constant range.
/// </summary>
public interface IGpuComputePipelineFactory {
    /// <summary>Creates a compute pipeline.</summary>
    /// <param name="deviceContext">The device to create the pipeline on.</param>
    /// <param name="computeShaderModule">The compiled compute shader module.</param>
    /// <param name="description">The pipeline's descriptor bindings, push-constant range, and sampler filter.</param>
    /// <returns>The created compute pipeline.</returns>
    IGpuComputePipeline Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description);
}
