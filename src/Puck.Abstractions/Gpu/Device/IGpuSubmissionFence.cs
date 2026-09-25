namespace Puck.Abstractions.Gpu;

/// <summary>
/// A reusable CPU-visible completion fence for one queue submission at a time — the primitive a frame-ring
/// host uses to know a PAST frame's submission retired before rewriting that frame slot's resources (command
/// buffer, host-visible per-frame buffers, descriptor sets). Created by
/// <see cref="IGpuQueueSubmitter.CreateSubmissionFence"/>, armed by the fenced
/// <see cref="IGpuQueueSubmitter.Submit(ReadOnlySpan{nint}, IGpuSubmissionFence)"/> overload,
/// and drained by <see cref="Wait"/>. One submission may be outstanding per fence: wait before re-arming.
/// </summary>
public interface IGpuSubmissionFence : IDisposable {
    /// <summary>Gets whether no submission armed with this fence is still running: either none is outstanding (never
    /// armed, or already waited) or the outstanding one has retired on the GPU. Reading it never blocks and never
    /// re-arms the fence, so a submission found retired is still outstanding until <see cref="Wait"/>.</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    bool IsSignaled { get; }

    /// <summary>Blocks until the last submission armed with this fence has retired on the GPU, then re-arms the
    /// fence for reuse. A no-op when no submission is outstanding (never armed, or already waited).</summary>
    void Wait();
}
