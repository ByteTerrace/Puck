namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral graphics pipelines from shaders and a render target.
/// </summary>
public interface IGpuPipelineFactory {
    /// <summary>Creates a graphics pipeline sized to the given width and height.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="renderTarget">The render target the pipeline will render into.</param>
    /// <param name="vertexShaderModule">The vertex shader module.</param>
    /// <param name="fragmentShaderModule">The fragment shader module.</param>
    /// <param name="pushConstantBinding">The push constant range, or <see langword="null"/> for none.</param>
    /// <param name="textureSamplerCount">The number of combined image-sampler descriptors.</param>
    /// <param name="enableStorageBuffer">Whether to include a storage buffer binding.</param>
    /// <param name="width">The viewport width, in pixels.</param>
    /// <param name="height">The viewport height, in pixels.</param>
    /// <returns>A new, owning <see cref="IGpuPipeline"/>.</returns>
    IGpuPipeline Create(
        IGpuDeviceContext deviceContext,
        IGpuRenderTarget renderTarget,
        IGpuShaderModule vertexShaderModule,
        IGpuShaderModule fragmentShaderModule,
        GpuPushConstantBinding? pushConstantBinding,
        uint textureSamplerCount,
        bool enableStorageBuffer,
        uint width,
        uint height
    );
}
