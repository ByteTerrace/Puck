namespace Puck.Abstractions.Gpu;

/// <summary>
/// Submits recorded command buffers to the device's single graphics/compute queue — the one queue each backend's
/// device context exposes (the Vulkan logical device's graphics queue; the Direct3D 12 context's direct command
/// queue) — so every submission against a device context is serialized on the same queue. The submitter keeps a list of
/// external waits (<see cref="AddExternalWait"/>) that its next submission carries and then clears.
/// </summary>
public interface IGpuQueueSubmitter {
    /// <summary>Adds a wait to the list the next submission carries: that submission, and on Direct3D 12 everything
    /// after it on the queue, starts only once the shared fence reaches the value. A consumer adds the wait for the value
    /// a producer's write signals when it acquires the image, before it records the submission that samples it, so the
    /// order is kept on the GPU and nothing blocks. A submission with no command buffers keeps the list for the next one.
    /// Called on the thread that submits.</summary>
    /// <param name="wait">The fence, created on or imported into this device, and the value to wait for.</param>
    /// <exception cref="ArgumentException">The wait's value is zero, or its fence belongs to another backend.</exception>
    void AddExternalWait(GpuExternalWait wait);
    /// <summary>Submits one or more command buffers to the graphics queue and returns without waiting; the work
    /// completes asynchronously.</summary>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void Submit(ReadOnlySpan<nint> commandBufferHandles);
    /// <summary>Submits one or more command buffers to the graphics queue WITHOUT waiting, arming
    /// <paramref name="fence"/> to signal when this submission retires — the frame-ring submit. The fence must not
    /// have another submission outstanding (call <see cref="IGpuSubmissionFence.Wait"/> first).</summary>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    /// <param name="fence">The submission fence to arm (created by <see cref="CreateSubmissionFence"/> on the same device).</param>
    void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence);
    /// <summary>Submits one or more command buffers and blocks until the submitted work has drained (Vulkan waits for
    /// the queue to go idle; Direct3D 12 waits for the whole device).</summary>
    /// <param name="commandBufferHandles">The command buffer handles to submit.</param>
    void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles);
    /// <summary>Creates a reusable submission fence on the bound device, for the fenced
    /// <see cref="Submit(ReadOnlySpan{nint}, IGpuSubmissionFence)"/> overload.</summary>
    /// <returns>The created fence (unsignaled; waiting before any fenced submit is a no-op).</returns>
    IGpuSubmissionFence CreateSubmissionFence();
}
