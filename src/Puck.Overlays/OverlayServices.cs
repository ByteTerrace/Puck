using Puck.Abstractions.Gpu;

namespace Puck.Overlays;

/// <summary>
/// The neutral GPU services bundle an overlay render decorator draws through — the library's counterpart to
/// <c>Puck.Demo.SdfProducerServices</c>, made backend-neutral. World registers exactly one backend
/// (<c>Puck.World.WorldHost.AddWorldGpuHost</c>), so <see cref="Build"/> resolves <see cref="IGpuDeviceContext"/>
/// directly instead of casting up from a backend-specific interface (the Demo Vulkan-only pattern in
/// <c>Puck.Demo.SdfParityProducers.BuildVulkanServices</c>).
/// </summary>
public sealed record OverlayServices {
    /// <summary>The shader bytecode file extension for the resolved backend (".spv" or ".dxil") — the overlay's
    /// counterpart of <c>Puck.SdfVm.SdfWorldRenderBuilder.BytecodeExtension</c>, carried alongside the services so a
    /// caller never re-derives it from the backend flag a second time.</summary>
    public required string BytecodeExtension { get; init; }
    /// <summary>The command recorder the compositor drives.</summary>
    public required IGpuCommandRecorder CommandRecorder { get; init; }
    /// <summary>Creates the render target to draw into, at the caller's chosen size. Invoked lazily on the first
    /// frame (once the backend's device exists); the caller disposes the result.</summary>
    public required Func<uint, uint, IGpuRenderTarget> CreateRenderTarget { get; init; }
    /// <summary>The descriptor pool/set allocator.</summary>
    public required IGpuDescriptorAllocator DescriptorAllocator { get; init; }
    /// <summary>The device context to render on.</summary>
    public required IGpuDeviceContext DeviceContext { get; init; }
    /// <summary>The graphics pipeline factory.</summary>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <summary>The queue submitter.</summary>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <summary>The shader module factory.</summary>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <summary>The descriptor binding/slot the program storage buffer is written to (Vulkan descriptor binding 1 —
    /// binding 0 is the combined-image sampler slot; Direct3D 12 table slot 0).</summary>
    public required uint StorageBufferBinding { get; init; }
    /// <summary>The storage buffer factory.</summary>
    public required IGpuStorageBufferFactory StorageBufferFactory { get; init; }
    /// <summary>The surface transfer factory, used to create the readback for capture.</summary>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }
    /// <summary>The vertex buffer factory.</summary>
    public required IGpuVertexBufferFactory VertexBufferFactory { get; init; }

    /// <summary>Resolves the neutral <see cref="OverlayServices"/> bundle for a same-device overlay producer on
    /// World's single registered backend.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories
    /// and the one registered <see cref="IGpuDeviceContext"/> — see <c>Puck.World.WorldHost.AddWorldGpuHost</c>,
    /// which registers only the launch-selected backend so this resolution can never disagree with it).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 — selects the bytecode
    /// extension and the storage-buffer binding/slot convention.</param>
    /// <returns>The resolved services bundle.</returns>
    public static OverlayServices Build(IServiceProvider serviceProvider, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        T Resolve<T>() => (T)serviceProvider.GetService(serviceType: typeof(T))!;

        var deviceContext = Resolve<IGpuDeviceContext>();
        var renderTargetFactory = Resolve<IGpuRenderTargetFactory>();

        return new OverlayServices {
            BytecodeExtension = (hostsOnDirectX ? ".dxil" : ".spv"),
            CommandRecorder = Resolve<IGpuCommandRecorder>(),
            CreateRenderTarget = (width, height) => renderTargetFactory.Create(deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: height, width: width),
            DescriptorAllocator = Resolve<IGpuDescriptorAllocator>(),
            DeviceContext = deviceContext,
            PipelineFactory = Resolve<IGpuPipelineFactory>(),
            QueueSubmitter = Resolve<IGpuQueueSubmitter>(),
            ShaderModuleFactory = Resolve<IGpuShaderModuleFactory>(),
            StorageBufferBinding = (hostsOnDirectX ? 0u : 1u),
            StorageBufferFactory = Resolve<IGpuStorageBufferFactory>(),
            SurfaceTransferFactory = Resolve<IGpuSurfaceTransferFactory>(),
            VertexBufferFactory = Resolve<IGpuVertexBufferFactory>(),
        };
    }
}
