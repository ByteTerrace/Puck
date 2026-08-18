using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// The backend-neutral GPU services a <see cref="FullscreenPassNode"/> draws through: one fullscreen-triangle
/// graphics pass over an inner render node's output. Not <see cref="IGpuComputeServices"/>, which carries no graphics
/// pipeline factory. A composition root resolves every member from the one backend it registered.
/// </summary>
public interface IFullscreenPassServices {
    /// <summary>Gets the command recorder the pass draws through.</summary>
    IGpuCommandRecorder CommandRecorder { get; }
    /// <summary>Gets the factory for the render target the pass draws into, at the pass's own size; the pass disposes
    /// the result.</summary>
    Func<uint, uint, IGpuRenderTarget> CreateRenderTarget { get; }
    /// <summary>Gets the descriptor pool/set allocator.</summary>
    IGpuDescriptorAllocator DescriptorAllocator { get; }
    /// <summary>Gets the device context to render on — the same device the inner node renders on.</summary>
    IGpuDeviceContext DeviceContext { get; }
    /// <summary>Gets the graphics pipeline factory.</summary>
    IGpuPipelineFactory PipelineFactory { get; }
    /// <summary>Gets the queue submitter.</summary>
    IGpuQueueSubmitter QueueSubmitter { get; }
    /// <summary>Gets the shader module factory.</summary>
    IGpuShaderModuleFactory ShaderModuleFactory { get; }
    /// <summary>Gets the vertex buffer factory.</summary>
    IGpuVertexBufferFactory VertexBufferFactory { get; }
}
