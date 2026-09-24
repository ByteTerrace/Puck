using Puck.Abstractions.Gpu;

namespace Puck.Overlays;

/// <summary>
/// The backend-neutral GPU service bundle an overlay render decorator draws through. World registers exactly one
/// backend (the launch-selected presenter registration in <c>Puck.Launcher.Windows</c>/<c>Puck.Launcher.Linux</c>),
/// so <see cref="Build"/> resolves <see cref="IGpuDeviceContext"/> directly, with no cast up from a
/// backend-specific interface.
/// </summary>
public sealed record OverlayServices {
    /// <summary>The shader bytecode file extension for the resolved backend (".spv" or ".dxil"), carried alongside
    /// the services so a caller never re-derives it from the backend flag a second time.</summary>
    public required string BytecodeExtension { get; init; }
    /// <summary>The factory for the command buffer the overlay records its pass into.</summary>
    public required IGpuComputeCommandPoolFactory CommandPoolFactory { get; init; }
    /// <summary>The command recorder the compositor drives.</summary>
    public required IGpuCommandRecorder CommandRecorder { get; init; }
    /// <summary>The descriptor pool/set allocator.</summary>
    public required IGpuDescriptorAllocator DescriptorAllocator { get; init; }
    /// <summary>The device context to render on.</summary>
    public required IGpuDeviceContext DeviceContext { get; init; }
    /// <summary>The host's <see cref="OverlayHudElementKind.Frame"/> content seam — every produced frame's
    /// <see cref="OverlayFrameSlots"/> table acquires each visible <c>Frame</c> element's lease through this. A host
    /// with no live frame content wires a null object that always answers <see langword="false"/>.</summary>
    public required IOverlayFrameSources FrameSources { get; init; }
    /// <summary>The factory for the fullscreen triangle's vertex buffer.</summary>
    public required IGpuGeometryBufferFactory GeometryBufferFactory { get; init; }
    /// <summary>The factory for the image the overlay draws into, created at the caller's chosen size on the first frame,
    /// once the backend's device exists.</summary>
    public required IGpuImageFactory ImageFactory { get; init; }
    /// <summary>The graphics pipeline factory.</summary>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <summary>The queue submitter.</summary>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <summary>The factory for the render pass the overlay draws in and the framebuffer that binds its image.</summary>
    public required IGpuRenderPassFactory RenderPassFactory { get; init; }
    /// <summary>The shader module factory.</summary>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <summary>The descriptor binding/slot the program storage buffer is written to — <c>1 + OverlayFrameSlots.SlotCount</c>
    /// on BOTH backends, immediately after the inner world image's binding (0) and the frame-slot bindings
    /// (<c>1..OverlayFrameSlots.SlotCount</c>): Vulkan declares that many scalar combined-image-sampler bindings
    /// followed by the storage buffer at the next binding number (<c>VulkanGraphicsPipelineFactory.BuildDescriptorBindings</c>);
    /// the Direct3D 12 graphics root signature packs its descriptor table [t0..tN-1 texture SRVs, then the storage SRV
    /// at t-textureSamplerCount] with an IDENTITY binding→slot map (see <c>DirectXGpuPipelineFactory.BuildLayout</c>),
    /// so both backends agree on the number.</summary>
    public required uint StorageBufferBinding { get; init; }
    /// <summary>The storage buffer factory.</summary>
    public required IGpuStorageBufferFactory StorageBufferFactory { get; init; }
    /// <summary>The surface transfer factory, used to create the readback for capture.</summary>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }

    /// <summary>Resolves the neutral <see cref="OverlayServices"/> bundle for a same-device overlay producer on
    /// World's single registered backend.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories
    /// and the one registered <see cref="IGpuDeviceContext"/> — the launch-selected presenter registration
    /// registers only that one backend so this resolution can never disagree with it).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 — selects the bytecode
    /// extension and the storage-buffer binding/slot convention.</param>
    /// <returns>The resolved services bundle.</returns>
    public static OverlayServices Build(IServiceProvider serviceProvider, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        T Resolve<T>() => ((T)serviceProvider.GetService(serviceType: typeof(T))!);

        return new OverlayServices {
            BytecodeExtension = ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX),
            CommandPoolFactory = Resolve<IGpuComputeCommandPoolFactory>(),
            CommandRecorder = Resolve<IGpuCommandRecorder>(),
            DescriptorAllocator = Resolve<IGpuDescriptorAllocator>(),
            DeviceContext = Resolve<IGpuDeviceContext>(),
            FrameSources = Resolve<IOverlayFrameSources>(),
            GeometryBufferFactory = Resolve<IGpuGeometryBufferFactory>(),
            ImageFactory = Resolve<IGpuImageFactory>(),
            PipelineFactory = Resolve<IGpuPipelineFactory>(),
            QueueSubmitter = Resolve<IGpuQueueSubmitter>(),
            RenderPassFactory = Resolve<IGpuRenderPassFactory>(),
            ShaderModuleFactory = Resolve<IGpuShaderModuleFactory>(),
            StorageBufferBinding = ((uint)(1 + OverlayFrameSlots.SlotCount)),
            StorageBufferFactory = Resolve<IGpuStorageBufferFactory>(),
            SurfaceTransferFactory = Resolve<IGpuSurfaceTransferFactory>(),
        };
    }
}
