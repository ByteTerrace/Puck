namespace Puck.Abstractions.Gpu;

/// <summary>
/// A cohesive bundle of the backend-neutral GPU services a compute render node drives, each bound to one device —
/// the pipeline, image, buffer, shader-module, and command-pool factories plus the recorder, bindings, queue
/// submitter, and surface-transfer factory. A node injects (or resolves) this one service instead of the nine
/// individual factories, folding the per-node constructor/resolution sprawl. The granular <c>IGpu*Factory</c>
/// interfaces remain registered and injectable on their own — this is an additive convenience over them, not a
/// replacement, so a node that needs exactly one factory can still depend on just that one.
/// <para>
/// Exposed as properties (rather than a multiply-inherited role interface) because the granular factories each have
/// a <c>Create</c> method: a property bundle keeps every <c>Create</c> call unambiguous at the call site
/// (<c>services.ImageFactory.Create(...)</c>).
/// </para>
/// </summary>
public interface IGpuComputeServices {
    /// <summary>The compute command-pool factory.</summary>
    IGpuCommandPoolFactory CommandPoolFactory { get; }
    /// <summary>The pipeline factory.</summary>
    IGpuPipelineFactory PipelineFactory { get; }
    /// <summary>The command recorder.</summary>
    IGpuRecorder Recorder { get; }
    /// <summary>The descriptor pools, sets, samplers and writes.</summary>
    IGpuBindings Bindings { get; }
    /// <summary>The queue submitter.</summary>
    IGpuQueueSubmitter QueueSubmitter { get; }
    /// <summary>The shader-module factory.</summary>
    IGpuShaderModuleFactory ShaderModuleFactory { get; }
    /// <summary>The buffer factory.</summary>
    IGpuBufferFactory BufferFactory { get; }
    /// <summary>The image factory.</summary>
    IGpuImageFactory ImageFactory { get; }
    /// <summary>The surface-transfer factory (readback/upload).</summary>
    IGpuSurfaceTransferFactory SurfaceTransferFactory { get; }
}
