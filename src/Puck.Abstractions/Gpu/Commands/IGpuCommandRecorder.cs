namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records draw commands into a command buffer — the backend-neutral subset of command-recording operations
/// the render nodes use.
/// </summary>
public interface IGpuCommandRecorder {
    /// <summary>Begins recording into the command buffer identified by the given handle.</summary>
    void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Ends recording of a command buffer.</summary>
    void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Opens a named debug-marker group scoping the commands recorded until the matching
    /// <see cref="EndDebugGroup"/> — surfaced by GPU capture tools (RenderDoc / PIX / Nsight) as a labeled scope. Maps
    /// to <c>vkCmdBeginDebugUtilsLabelEXT</c> / a Direct3D 12 PIX event; a no-op when the backend's debug-label
    /// facility is unavailable. Records no GPU work and never affects rendered output, so it is safe on every path.</summary>
    void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label);
    /// <summary>Closes the most recently opened <see cref="BeginDebugGroup"/> on the command buffer.</summary>
    void EndDebugGroup(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Begins a render pass instance for the given framebuffer and render pass.</summary>
    void BeginRenderPass(
        nint deviceHandle,
        nint commandBufferHandle,
        nint renderPassHandle,
        nint framebufferHandle,
        uint width,
        uint height
    );
    /// <summary>Ends the current render pass instance.</summary>
    void EndRenderPass(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Binds a pipeline to the graphics bind point.</summary>
    void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle);
    /// <summary>Binds a vertex buffer at binding number 0.</summary>
    void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint vertexBufferHandle);
    /// <summary>Binds a single descriptor set at set number 0 for the graphics bind point.</summary>
    void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle);
    /// <summary>Records an update of a range of the push constant block.</summary>
    void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        GpuShaderStage stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    );
    /// <summary>Records a dynamic scissor rectangle for viewport 0.</summary>
    void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height);
    /// <summary>Records a non-indexed draw.</summary>
    void Draw(nint deviceHandle, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
}
