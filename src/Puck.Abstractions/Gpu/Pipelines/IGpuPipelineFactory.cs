namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates compute and graphics pipelines on the device its backend implementation is bound to.
/// </summary>
public interface IGpuPipelineFactory {
    /// <summary>Creates a compute pipeline from a compiled compute shader, an ordered set of descriptor bindings
    /// (set 0), and an optional push-constant range.</summary>
    /// <param name="computeShaderModule">The compiled compute shader module.</param>
    /// <param name="description">The pipeline's descriptor bindings, push-constant range, and sampler filter.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuComputePipeline"/>.</returns>
    IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name);
    /// <summary>Creates a graphics pipeline for a render pass. The pipeline carries no extent: the viewport and scissor
    /// are set when a render pass begins (<see cref="IGpuRecorder.BeginRenderPass"/>), so one pipeline draws into
    /// framebuffers of any size.</summary>
    /// <param name="renderPass">The render pass the pipeline draws in; its attachment formats fix the pipeline's
    /// outputs, and the pipeline draws in any framebuffer made for it.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="description">The pipeline's vertex input, sampler, storage-buffer, and push-constant shape.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>A new, owning <see cref="IGpuPipeline"/>.</returns>
    IGpuPipeline Create(
        IGpuRenderPass renderPass,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        in GpuObjectName name
    );
}
