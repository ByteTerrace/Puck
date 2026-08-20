using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.World;

/// <summary>The DI-resolved <see cref="IFullscreenPassServices"/> World hands every composed post-render pass — the
/// same resolution shape <c>Puck.Overlays.OverlayServices.Build</c> uses for the unified overlay, since World
/// registers exactly one backend and both draw a fullscreen graphics pass over an existing render target.</summary>
internal sealed record WorldPostRenderExtensionServices : IFullscreenPassServices {
    /// <inheritdoc/>
    public required IGpuCommandRecorder CommandRecorder { get; init; }
    /// <inheritdoc/>
    public required Func<uint, uint, IGpuRenderTarget> CreateRenderTarget { get; init; }
    /// <inheritdoc/>
    public required IGpuDescriptorAllocator DescriptorAllocator { get; init; }
    /// <inheritdoc/>
    public required IGpuDeviceContext DeviceContext { get; init; }
    /// <inheritdoc/>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <inheritdoc/>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }
    /// <inheritdoc/>
    public required IGpuVertexBufferFactory VertexBufferFactory { get; init; }

    /// <summary>Resolves the services bundle for a post-render pass on World's single registered backend.</summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <returns>The resolved services bundle.</returns>
    public static WorldPostRenderExtensionServices Build(IServiceProvider serviceProvider) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        T Resolve<T>() => ((T)serviceProvider.GetService(serviceType: typeof(T))!);

        var deviceContext = Resolve<IGpuDeviceContext>();
        var renderTargetFactory = Resolve<IGpuRenderTargetFactory>();

        return new WorldPostRenderExtensionServices {
            CommandRecorder = Resolve<IGpuCommandRecorder>(),
            CreateRenderTarget = (width, height) => renderTargetFactory.Create(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            width: width
        ),
            DescriptorAllocator = Resolve<IGpuDescriptorAllocator>(),
            DeviceContext = deviceContext,
            PipelineFactory = Resolve<IGpuPipelineFactory>(),
            QueueSubmitter = Resolve<IGpuQueueSubmitter>(),
            ShaderModuleFactory = Resolve<IGpuShaderModuleFactory>(),
            SurfaceTransferFactory = Resolve<IGpuSurfaceTransferFactory>(),
            VertexBufferFactory = Resolve<IGpuVertexBufferFactory>(),
        };
    }
}
