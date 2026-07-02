namespace Puck.Abstractions.Gpu;

/// <summary>
/// Submits recorded command buffers to the device's single graphics/compute queue — the one queue each backend's
/// device context exposes (the Vulkan logical device's graphics queue; the Direct3D 12 context's direct command
/// queue) — so every submission against a device context is serialized on the same queue.
/// </summary>
public interface IGpuQueueSubmitter {
    /// <summary>Submits one or more command buffers to the graphics queue and returns without waiting; the work
    /// completes asynchronously.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles);
    /// <summary>Submits one or more command buffers and blocks until the submitted work has drained (Vulkan waits for
    /// the queue to go idle; Direct3D 12 waits for the whole device).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles);
}
