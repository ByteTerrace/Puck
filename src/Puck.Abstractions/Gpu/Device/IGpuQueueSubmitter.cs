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
    /// <summary>Submits one or more command buffers to the graphics queue WITHOUT waiting, arming
    /// <paramref name="fence"/> to signal when this submission retires — the frame-ring submit. The fence must not
    /// have another submission outstanding (call <see cref="IGpuSubmissionFence.Wait"/> first).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    /// <param name="fence">The submission fence to arm (created by <see cref="CreateSubmissionFence"/> on the same device).</param>
    void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence);
    /// <summary>Submits one or more command buffers and blocks until the submitted work has drained (Vulkan waits for
    /// the queue to go idle; Direct3D 12 waits for the whole device).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles);
    /// <summary>Creates a reusable submission fence on <paramref name="deviceContext"/>'s device, for the fenced
    /// <see cref="Submit(IGpuDeviceContext, ReadOnlySpan{nint}, IGpuSubmissionFence)"/> overload.</summary>
    /// <param name="deviceContext">The GPU device context the fence belongs to.</param>
    /// <returns>The created fence (unsignaled; waiting before any fenced submit is a no-op).</returns>
    IGpuSubmissionFence CreateSubmissionFence(IGpuDeviceContext deviceContext);
}
