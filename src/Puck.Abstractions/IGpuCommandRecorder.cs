namespace Puck.Abstractions;

/// <summary>
/// Records draw commands into a command buffer — the backend-neutral subset of command-recording operations
/// the render nodes use.
/// </summary>
public interface IGpuCommandRecorder {
    /// <summary>Begins recording into the command buffer identified by the given handle.</summary>
    void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle);
    /// <summary>Ends recording of a command buffer.</summary>
    void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle);
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
        uint stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    );
    /// <summary>Records a dynamic scissor rectangle for viewport 0.</summary>
    void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height);
    /// <summary>Records a non-indexed draw.</summary>
    void Draw(nint deviceHandle, nint commandBufferHandle, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
}
