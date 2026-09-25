using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.World;

/// <summary>The DI-resolved <see cref="IFullscreenPassServices"/> World hands every composed post-render pass — the
/// same resolution shape <c>Puck.Overlays.OverlayServices.Build</c> uses for the unified overlay, since World
/// registers exactly one backend and both draw a fullscreen graphics pass.</summary>
internal sealed record WorldPostRenderExtensionServices : IFullscreenPassServices {
    /// <inheritdoc/>
    public required IGpuRecorder Recorder { get; init; }
    /// <inheritdoc/>
    public required IGpuComputeServices ComputeServices { get; init; }
    /// <inheritdoc/>
    public required IGpuBindings Bindings { get; init; }
    /// <inheritdoc/>
    public required IGpuDeviceContext DeviceContext { get; init; }
    /// <inheritdoc/>
    public required IGpuBufferFactory BufferFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <inheritdoc/>
    public required IGpuRenderPassFactory RenderPassFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }

    /// <summary>Resolves the services bundle for a post-render pass on World's single registered backend.</summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <returns>The resolved services bundle.</returns>
    public static WorldPostRenderExtensionServices Build(IServiceProvider serviceProvider) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        T Resolve<T>() => ((T)serviceProvider.GetService(serviceType: typeof(T))!);

        return new WorldPostRenderExtensionServices {
            ComputeServices = Resolve<IGpuComputeServices>(),
            Recorder = Resolve<IGpuRecorder>(),
            Bindings = Resolve<IGpuBindings>(),
            DeviceContext = Resolve<IGpuDeviceContext>(),
            BufferFactory = Resolve<IGpuBufferFactory>(),
            PipelineFactory = Resolve<IGpuPipelineFactory>(),
            QueueSubmitter = Resolve<IGpuQueueSubmitter>(),
            RenderPassFactory = Resolve<IGpuRenderPassFactory>(),
            ShaderModuleFactory = Resolve<IGpuShaderModuleFactory>(),
            SurfaceTransferFactory = Resolve<IGpuSurfaceTransferFactory>(),
        };
    }
}
