namespace Puck.Abstractions.Gpu;

/// <summary>
/// The neutral GPU services of one device, every one bound to it: the recorder, the bindings, the queue submitter, and
/// the factories for command pools, pipelines, shader modules, buffers, images, render passes and surface transfers,
/// and the naming their objects are created with.
/// A backend creates the set with its device context and hands it out as <see cref="IGpuDeviceContext.Services"/>, the
/// one way a consumer reaches a device-bound service; no member takes a device argument. The set is bound to the
/// context rather than to one native device, so it survives a device recreated in place after a loss.
/// <para>
/// A property set rather than one interface inheriting every service, because several services declare a
/// <c>Create</c> method: <c>services.ImageFactory.Create(...)</c> stays unambiguous at the call site.
/// <see cref="GpuWorkCounting.Wrap(GpuDeviceServices, GpuWorkLedger)"/> returns a set whose counted members count into
/// a ledger.
/// </para>
/// </summary>
public sealed class GpuDeviceServices {
    /// <summary>Gets the descriptor pools, sets, samplers and writes.</summary>
    public required IGpuBindings Bindings { get; init; }
    /// <summary>Gets the buffer factory.</summary>
    public required IGpuBufferFactory BufferFactory { get; init; }
    /// <summary>Gets the command-pool factory.</summary>
    public required IGpuCommandPoolFactory CommandPoolFactory { get; init; }
    /// <summary>Gets the image factory.</summary>
    public required IGpuImageFactory ImageFactory { get; init; }
    /// <summary>Gets the compute and graphics pipeline factory.</summary>
    public required IGpuPipelineFactory PipelineFactory { get; init; }
    /// <summary>Gets the queue submitter.</summary>
    public required IGpuQueueSubmitter QueueSubmitter { get; init; }
    /// <summary>Gets the command recorder, for compute and graphics work alike.</summary>
    public required IGpuRecorder Recorder { get; init; }
    /// <summary>Gets the factory for render passes and the framebuffers that bind them to images.</summary>
    public required IGpuRenderPassFactory RenderPassFactory { get; init; }
    /// <summary>Gets the shader-module factory.</summary>
    public required IGpuShaderModuleFactory ShaderModuleFactory { get; init; }
    /// <summary>Gets the operator's faults this set's creations pass through (<see cref="GpuCreationFaults.Wrap"/>), or
    /// <see langword="null"/> when the host arms none. A holder that retries a refused build only when a recorded input
    /// changes records <see cref="GpuCreationFaults.Revision"/> as one of them, so arming or clearing a fault retries it.
    /// Every wrap carries it over unchanged.</summary>
    public GpuCreationFaults? Faults { get; init; }
    /// <summary>Gets the naming every creating member hands its objects to (<see cref="GpuObjectNaming"/>), or
    /// <see cref="GpuObjectNaming.Off"/>, the default, when the device names nothing. Every wrap carries it over
    /// unchanged.</summary>
    public GpuObjectNaming Naming { get; init; } = GpuObjectNaming.Off;
    /// <summary>Gets the surface-transfer factory (readback, upload and import).</summary>
    public required IGpuSurfaceTransferFactory SurfaceTransferFactory { get; init; }
}
