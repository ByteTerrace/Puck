namespace Puck.Abstractions;

/// <summary>
/// A cohesive bundle of the backend-neutral GPU services a compute render node drives — the compute pipeline,
/// storage-image/buffer, shader-module, and command-pool factories plus the recorder, descriptor allocator, queue
/// submitter, and surface-transfer factory. A node injects (or resolves) this ONE service instead of the nine
/// individual factories, folding the per-node constructor/resolution sprawl. The granular <c>IGpu*Factory</c>
/// interfaces remain registered and injectable on their own — this is an additive convenience over them, not a
/// replacement, so a node that needs exactly one factory can still depend on just that one.
/// <para>
/// Exposed as properties (rather than a multiply-inherited role interface) because the granular factories each have
/// a <c>Create</c> method: a property bundle keeps every <c>Create</c> call unambiguous at the call site
/// (<c>services.StorageImageFactory.Create(...)</c>).
/// </para>
/// </summary>
public interface IGpuComputeServices {
    /// <summary>The compute command-pool factory.</summary>
    IGpuComputeCommandPoolFactory CommandPoolFactory { get; }
    /// <summary>The compute pipeline factory.</summary>
    IGpuComputePipelineFactory ComputePipelineFactory { get; }
    /// <summary>The compute command recorder.</summary>
    IGpuComputeRecorder ComputeRecorder { get; }
    /// <summary>The descriptor pool/set allocator.</summary>
    IGpuDescriptorAllocator DescriptorAllocator { get; }
    /// <summary>The queue submitter.</summary>
    IGpuQueueSubmitter QueueSubmitter { get; }
    /// <summary>The shader-module factory.</summary>
    IGpuShaderModuleFactory ShaderModuleFactory { get; }
    /// <summary>The storage-buffer factory.</summary>
    IGpuStorageBufferFactory StorageBufferFactory { get; }
    /// <summary>The storage-image factory.</summary>
    IGpuStorageImageFactory StorageImageFactory { get; }
    /// <summary>The surface-transfer factory (readback/upload).</summary>
    IGpuSurfaceTransferFactory SurfaceTransferFactory { get; }
}
