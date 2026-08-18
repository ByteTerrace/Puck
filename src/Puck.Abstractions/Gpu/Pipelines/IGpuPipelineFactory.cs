namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral graphics pipelines from shaders and a render target.
/// </summary>
public interface IGpuPipelineFactory {
    /// <summary>Creates a graphics pipeline sized to the given width and height.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="renderTarget">The render target the pipeline will render into.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="description">The pipeline's vertex input, sampler, storage-buffer, and push-constant shape.</param>
    /// <param name="width">The viewport width, in pixels.</param>
    /// <param name="height">The viewport height, in pixels.</param>
    /// <returns>A new, owning <see cref="IGpuPipeline"/>.</returns>
    IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderTarget renderTarget,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuGraphicsPipelineDescription description,
        uint width,
        uint height
    );
}
