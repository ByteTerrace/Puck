using Puck.Abstractions;

namespace Puck.Demo;

/// <summary>
/// The backend's GPU services that drive an <see cref="SdfProducerNode"/>. The showcase fills this with one
/// backend's factories — Direct3D 12 (for the zero-copy cross-backend path) or Vulkan (same-device) — so the
/// neutral producer renders identically on either.
/// </summary>
public sealed record SdfProducerServices {
    /// <summary>The command recorder the compositor drives.</summary>
    public required IGpuCommandRecorder CommandRecorder { get; init; }
    /// <summary>The descriptor pool/set allocator.</summary>
    public required IGpuDescriptorAllocator DescriptorAllocator { get; init; }
    /// <summary>The device context to render on.</summary>
    public required IGpuDeviceContext DeviceContext { get; init; }
    /// <summary>Whether the producer owns (and disposes) <see cref="DeviceContext"/>.</summary>
    public required bool OwnsDeviceContext { get; init; }
    /// <summary>The graphics pipeline factory.</summary>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <summary>The queue submitter.</summary>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <summary>Creates the render target to draw into — an <see cref="IGpuExportableRenderTarget"/> to share zero-copy, or a plain target for same-device present. Invoked lazily on the first frame (once the backend's device exists); the producer disposes the result.</summary>
    public required Func<IGpuRenderTarget> CreateRenderTarget { get; init; }
    /// <summary>The shader module factory.</summary>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <summary>The descriptor binding/slot the program storage buffer is written to (Vulkan binding 1; Direct3D 12 table slot 0).</summary>
    public required uint StorageBufferBinding { get; init; }
    /// <summary>The storage buffer factory.</summary>
    public required IGpuStorageBufferFactory StorageBufferFactory { get; init; }
    /// <summary>The surface transfer factory, used to create the readback for capture.</summary>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }
    /// <summary>The vertex buffer factory.</summary>
    public required IGpuVertexBufferFactory VertexBufferFactory { get; init; }
}
