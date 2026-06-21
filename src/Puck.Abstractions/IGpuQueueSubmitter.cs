namespace Puck.Abstractions;

/// <summary>
/// Submits command buffers to the GPU queue.
/// </summary>
public interface IGpuQueueSubmitter {
    /// <summary>Submits one or more command buffers to the graphics queue without waiting.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles);
    /// <summary>Submits one or more command buffers and waits for the queue to become idle.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles);
}
