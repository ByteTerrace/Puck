namespace Puck.DirectX.Presentation;

/// <summary>
/// Records one back-buffer's draw commands into an already-opened D3D12 graphics command list. The caller
/// resets the allocator and command list before the call and closes/executes it after; the recorder owns the
/// render-target barrier pair, the RTV + viewport + scissor setup, and replaying the
/// <see cref="DirectXDrawCommand"/> list in sequence-key order.
/// </summary>
public interface IDirectXCommandListRecorder {
    /// <summary>
    /// Records the render-target transition, viewport, scissor, and every
    /// <see cref="DirectXDrawCommand"/> in <paramref name="drawCommands"/> into
    /// <paramref name="commandListHandle"/>, then emits the final transition to present state.
    /// </summary>
    /// <param name="commandListHandle">An open <c>ID3D12GraphicsCommandList*</c>.</param>
    /// <param name="backBufferHandle">The <c>ID3D12Resource*</c> of the current back buffer; used for the resource-barrier pair.</param>
    /// <param name="rtvCpuHandle">The <c>D3D12_CPU_DESCRIPTOR_HANDLE.ptr</c> of the back-buffer RTV, cast to <see cref="nint"/>.</param>
    /// <param name="viewportWidth">The back-buffer width in pixels.</param>
    /// <param name="viewportHeight">The back-buffer height in pixels.</param>
    /// <param name="drawCommands">The ordered list of draw commands to replay.</param>
    void RecordBackBuffer(
        nint commandListHandle,
        nint backBufferHandle,
        nint rtvCpuHandle,
        uint viewportWidth,
        uint viewportHeight,
        IReadOnlyList<DirectXDrawCommand> drawCommands
    );
}
