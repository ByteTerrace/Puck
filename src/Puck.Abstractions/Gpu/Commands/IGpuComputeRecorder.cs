namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records compute commands into a command buffer in a backend-neutral way: pipeline binding, descriptor binding,
/// push constants, dispatch, and the explicit image-layout and memory barriers the caller sequences (no barriers
/// are implied). The <see cref="GpuImageLayout"/>, <see cref="GpuComputeStage"/>, and <see cref="GpuComputeAccess"/>
/// values map to each backend's native synchronization primitives.
/// </summary>
public interface IGpuComputeRecorder {
    /// <summary>Begins recording into the command buffer.</summary>
    void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Ends recording of the command buffer.</summary>
    void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Opens a named debug-marker group scoping the commands recorded until the matching
    /// <see cref="EndDebugGroup"/> — surfaced by GPU capture tools (RenderDoc / PIX / Nsight) as a labeled scope. Maps
    /// to <c>vkCmdBeginDebugUtilsLabelEXT</c> / a Direct3D 12 PIX event; a no-op when the backend's debug-label
    /// facility is unavailable. Records no GPU work and never affects rendered output, so it is safe on every path.</summary>
    void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label);
    /// <summary>Closes the most recently opened <see cref="BeginDebugGroup"/> on the command buffer.</summary>
    void EndDebugGroup(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Binds a pipeline to the compute bind point.</summary>
    void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle);
    /// <summary>Binds the descriptor set at set 0 for the compute bind point.</summary>
    void BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle);
    /// <summary>Records an update of the push-constant range.</summary>
    void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data);
    /// <summary>Records a compute dispatch.</summary>
    void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ);
    /// <summary>Records an INDIRECT compute dispatch: the (x, y, z) group counts are read by the GPU from three
    /// consecutive <see langword="uint"/>s in <paramref name="argumentBufferHandle"/> at
    /// <paramref name="argumentBufferOffset"/> bytes, rather than supplied on the CPU. The argument buffer must have
    /// been created via <see cref="IGpuStorageBufferFactory.CreateIndirectArgs"/>. The 12-byte group-count layout is
    /// identical on both backends (Vulkan <c>VkDispatchIndirectCommand</c> / Direct3D 12 <c>D3D12_DISPATCH_ARGUMENTS</c>).</summary>
    void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset);
    /// <summary>Records a barrier transitioning a storage image between <see cref="GpuImageLayout"/> values over the given access and stage scopes.</summary>
    void TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask);
    /// <summary>Records a global memory barrier over the given access and stage scopes.</summary>
    void MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask);
    /// <summary>Records a barrier on a SPECIFIC buffer over the given access and stage scopes. Unlike
    /// <see cref="MemoryBarrier"/> (a global barrier), this targets one resource — required on Direct3D 12 to
    /// transition a GPU-written buffer into <c>INDIRECT_ARGUMENT</c> before an indirect dispatch reads it (a global
    /// barrier does not prepare a specific buffer's state for <c>ExecuteIndirect</c>). On Vulkan it is a memory barrier
    /// over the same scopes.</summary>
    void TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask);
}
