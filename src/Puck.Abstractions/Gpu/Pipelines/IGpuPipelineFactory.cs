namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral graphics pipelines from shaders and the render pass they draw in.
/// </summary>
public interface IGpuPipelineFactory {
    /// <summary>Creates a graphics pipeline for a render pass. The pipeline carries no extent: the viewport and scissor
    /// are set when a render pass begins (<see cref="IGpuRecorder.BeginRenderPass"/>), so one pipeline draws into
    /// framebuffers of any size.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="renderPass">The render pass the pipeline draws in; its attachment formats fix the pipeline's
    /// outputs, and the pipeline draws in any framebuffer made for it.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="description">The pipeline's vertex input, sampler, storage-buffer, and push-constant shape.</param>

    /// <returns>A new, owning <see cref="IGpuPipeline"/>.</returns>
    IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderPass renderPass,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description
    );
}
