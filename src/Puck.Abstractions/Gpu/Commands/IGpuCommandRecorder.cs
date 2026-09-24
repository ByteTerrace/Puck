namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records draw commands into a command buffer — the backend-neutral subset of command-recording operations
/// the render nodes use.
/// </summary>
public interface IGpuCommandRecorder {
    /// <summary>Begins recording into the command buffer identified by the given handle.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The backend-neutral command-buffer token.</param>
    void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Ends recording of a command buffer.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The backend-neutral command-buffer token.</param>
    void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Opens a named debug-marker group scoping the commands recorded until the matching
    /// <see cref="EndDebugGroup"/> — surfaced by GPU capture tools (RenderDoc / PIX / Nsight) as a labeled scope. Maps
    /// to <c>vkCmdBeginDebugUtilsLabelEXT</c> / a Direct3D 12 PIX event; a no-op when the backend's debug-label
    /// facility is unavailable. Records no GPU work and never affects rendered output, so it is safe on every path.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer receiving the marker.</param>
    /// <param name="label">The non-empty capture-tool label.</param>
    void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label);
    /// <summary>Closes the most recently opened <see cref="BeginDebugGroup"/> on the command buffer.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer receiving the marker.</param>
    void EndDebugGroup(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Begins a framebuffer's render pass over its whole extent: each attachment is cleared, loaded or
    /// discarded as the pass declares, and the viewport covers the extent.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="framebuffer">The framebuffer, created by the backend's <see cref="IGpuRenderPassFactory"/>.</param>
    void BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer);
    /// <summary>Ends the current render pass instance.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    void EndRenderPass(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Binds a pipeline to the graphics bind point.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="pipelineHandle">The backend-neutral pipeline token.</param>
    void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle);
    /// <summary>Binds a geometry buffer's vertices at vertex binding 0.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="bufferHandle">The <see cref="IGpuBuffer.BufferHandle"/> of a buffer created with
    /// <see cref="GpuBufferUsage.Vertex"/>.</param>
    /// <param name="sizeBytes">The bytes of the buffer the draw may read.</param>
    /// <param name="strideBytes">The bytes between consecutive vertices, as the pipeline's
    /// <see cref="GpuVertexInputLayout.StrideBytes"/> declares.</param>
    void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes);
    /// <summary>Binds a range of a geometry buffer as the indices the next indexed draw reads.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="bufferHandle">The <see cref="IGpuBuffer.BufferHandle"/> of a buffer created with
    /// <see cref="GpuBufferUsage.Index"/>.</param>
    /// <param name="offsetBytes">The byte offset of the first index, a multiple of four.</param>
    /// <param name="sizeBytes">The bytes of indices from that offset.</param>
    /// <param name="format">The width of each index.</param>
    void BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format);
    /// <summary>Records a draw of one instance of the triangle list the bound indices name, in index order, starting at
    /// the first bound index.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="indexCount">The number of indices drawn; positive.</param>
    void DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount);
    /// <summary>Binds a single descriptor set at set number 0 for the graphics bind point.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="pipelineLayoutHandle">The backend-neutral pipeline-layout token.</param>
    /// <param name="descriptorSetHandle">The backend-neutral descriptor-set token.</param>
    void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle);
    /// <summary>Records an update of a range of the push constant block.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="pipelineLayoutHandle">The backend-neutral pipeline-layout token.</param>
    /// <param name="stageFlags">The defined shader stages that consume the range.</param>
    /// <param name="offset">The four-byte-aligned byte offset.</param>
    /// <param name="data">The non-empty, four-byte-sized payload.</param>
    void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        GpuShaderStage stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    );
    /// <summary>Records a dynamic scissor rectangle for viewport 0.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="x">The left edge in pixels.</param>
    /// <param name="y">The top edge in pixels.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height);
    /// <summary>Records a non-indexed draw.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="commandBufferHandle">The command buffer to record.</param>
    /// <param name="parameters">The validated draw counts and offsets.</param>
    void Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters);
}
