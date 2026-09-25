namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records compute and graphics commands into a command buffer of the device the recorder was obtained from. No
/// barrier is implied: the caller records every image-layout, buffer and memory barrier its accesses need, and the
/// <see cref="GpuImageLayout"/>, <see cref="GpuStage"/> and <see cref="GpuAccess"/> values map to each
/// backend's native synchronization.
/// </summary>
public interface IGpuRecorder {
    /// <summary>Begins recording into a command buffer, discarding what it recorded before.</summary>
    /// <param name="commandBufferHandle">The command buffer, from an <see cref="IGpuCommandPool"/>.</param>
    void BeginCommandBuffer(nint commandBufferHandle);
    /// <summary>Ends recording of a command buffer.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    void EndCommandBuffer(nint commandBufferHandle);
    /// <summary>Opens a named debug-marker group scoping the commands recorded until the matching
    /// <see cref="EndDebugGroup"/>, which GPU capture tools show as a labeled scope. It maps to
    /// <c>vkCmdBeginDebugUtilsLabelEXT</c> and a Direct3D 12 PIX event, is a no-op when the backend's debug-label
    /// facility is unavailable, and records no GPU work.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="label">The non-empty capture-tool label.</param>
    void BeginDebugGroup(nint commandBufferHandle, string label);
    /// <summary>Closes the most recently opened <see cref="BeginDebugGroup"/> on the command buffer.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    void EndDebugGroup(nint commandBufferHandle);
    /// <summary>Begins a framebuffer's render pass. Each attachment is cleared, loaded or discarded as the pass
    /// declares, a depth attachment clearing to its <see cref="GpuDepthAttachment.ClearDepth"/>; a clear covers
    /// <paramref name="area"/> and leaves the pixels outside it as they were. The viewport and scissor cover
    /// <paramref name="area"/> with clip-space +y at its top edge.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="framebuffer">The framebuffer, from the same device's <see cref="IGpuRenderPassFactory"/>.</param>
    /// <param name="area">The pixels the pass draws, inside the framebuffer's extent, or <see langword="null"/> for the
    /// whole extent. A later <see cref="SetScissor"/> narrows the scissor within it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="area"/> is empty or reaches outside the
    /// framebuffer.</exception>
    void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null);
    /// <summary>Ends the current render pass.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    void EndRenderPass(nint commandBufferHandle);
    /// <summary>Binds a pipeline to its bind point.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bindPoint">The bind point: <see cref="GpuBindPoint.Graphics"/> for an <see cref="IGpuPipeline"/>,
    /// <see cref="GpuBindPoint.Compute"/> for an <see cref="IGpuComputePipeline"/>.</param>
    /// <param name="pipelineHandle">The pipeline's <c>Handle</c>.</param>
    void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle);
    /// <summary>Binds a descriptor set at set 0 of a bind point.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bindPoint">The bind point of the pipeline the set is read by.</param>
    /// <param name="pipelineLayoutHandle">That pipeline's layout.</param>
    /// <param name="descriptorSetHandle">The descriptor set, allocated against that layout.</param>
    void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle);
    /// <summary>Records an update of a range of a bind point's push-constant block.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bindPoint">The bind point of the pipeline the range is read by.</param>
    /// <param name="pipelineLayoutHandle">That pipeline's layout.</param>
    /// <param name="stageFlags">The defined shader stages that read the range, as the layout declares them.</param>
    /// <param name="offset">The byte offset, a multiple of four.</param>
    /// <param name="data">The non-empty payload, a multiple of four bytes.</param>
    void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data);
    /// <summary>Binds a geometry buffer's vertices at vertex binding 0.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bufferHandle">The <see cref="IGpuBuffer.BufferHandle"/> of a buffer created with
    /// <see cref="GpuBufferUsage.Vertex"/>.</param>
    /// <param name="sizeBytes">The bytes of the buffer the draw may read.</param>
    /// <param name="strideBytes">The bytes between consecutive vertices, as the pipeline's
    /// <see cref="GpuVertexInputLayout.StrideBytes"/> declares.</param>
    void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes);
    /// <summary>Binds a range of a geometry buffer as the indices the next indexed draw reads.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bufferHandle">The <see cref="IGpuBuffer.BufferHandle"/> of a buffer created with
    /// <see cref="GpuBufferUsage.Index"/>.</param>
    /// <param name="offsetBytes">The byte offset of the first index, a multiple of four.</param>
    /// <param name="sizeBytes">The bytes of indices from that offset.</param>
    /// <param name="format">The width of each index.</param>
    void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format);
    /// <summary>Narrows the scissor of the current render pass to a rectangle.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="rect">The rectangle, in the framebuffer's pixels.</param>
    void SetScissor(nint commandBufferHandle, GpuPixelRect rect);
    /// <summary>Records a non-indexed draw.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="parameters">The validated draw counts and offsets.</param>
    void Draw(nint commandBufferHandle, in GpuDrawParameters parameters);
    /// <summary>Records a draw of one instance of the triangle list the bound indices name, in index order, starting at
    /// the first bound index.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="indexCount">The number of indices drawn; positive.</param>
    void DrawIndexed(nint commandBufferHandle, uint indexCount);
    /// <summary>Records a compute dispatch.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="groupCountX">The X group count.</param>
    /// <param name="groupCountY">The Y group count.</param>
    /// <param name="groupCountZ">The Z group count.</param>
    void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ);
    /// <summary>Records an indirect compute dispatch whose (x, y, z) group counts the GPU reads from three consecutive
    /// <see langword="uint"/>s of an argument buffer, the layout of both Vulkan's <c>VkDispatchIndirectCommand</c> and
    /// Direct3D 12's <c>D3D12_DISPATCH_ARGUMENTS</c>.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="argumentBufferHandle">The argument buffer, created with <see cref="GpuBufferUsage.Indirect"/>.</param>
    /// <param name="argumentBufferOffset">The byte offset of the three group counts.</param>
    void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset);
    /// <summary>Records a zero clear of a color image in <see cref="GpuImageLayout.General"/>.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="imageHandle">The image, created with <see cref="GpuImageUsage.Storage"/>.</param>
    /// <param name="format">The image's format: RGBA8, BGRA8, RGBA16F or RGBA32F.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not one of those.</exception>
    void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format);
    /// <summary>Records a zero fill of a whole storage buffer, ordered before the next compute shader access.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bufferHandle">The buffer.</param>
    /// <param name="sizeBytes">The buffer's size in bytes; positive and a multiple of four.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeBytes"/> is zero or not a multiple of
    /// four.</exception>
    void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes);
    /// <summary>Records a barrier moving an image between layouts over the given access and stage scopes.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="imageHandle">The image.</param>
    /// <param name="oldLayout">The layout the image is in.</param>
    /// <param name="newLayout">The layout the following accesses need.</param>
    /// <param name="sourceAccessMask">The prior accesses.</param>
    /// <param name="destinationAccessMask">The following accesses.</param>
    /// <param name="sourceStageMask">The prior stages.</param>
    /// <param name="destinationStageMask">The following stages.</param>
    void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask);
    /// <summary>Records a global memory barrier over the given access and stage scopes.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="sourceAccessMask">The prior accesses.</param>
    /// <param name="destinationAccessMask">The following accesses.</param>
    /// <param name="sourceStageMask">The prior stages.</param>
    /// <param name="destinationStageMask">The following stages.</param>
    void MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask);
    /// <summary>Records a barrier on one buffer over the given access and stage scopes. Direct3D 12 needs it, rather
    /// than <see cref="MemoryBarrier"/>, to move a GPU-written buffer into <c>INDIRECT_ARGUMENT</c> before an indirect
    /// dispatch reads it; on Vulkan it is a buffer memory barrier over the same scopes.</summary>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="bufferHandle">The buffer.</param>
    /// <param name="sourceAccessMask">The prior accesses.</param>
    /// <param name="destinationAccessMask">The following accesses.</param>
    /// <param name="sourceStageMask">The prior stages.</param>
    /// <param name="destinationStageMask">The following stages.</param>
    void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask);
}
