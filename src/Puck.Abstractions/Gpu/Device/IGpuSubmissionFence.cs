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
    /// <exception cref="GpuSubmissionAbandonedException">The submission never reached the queue, so nothing proves it
    /// ran.</exception>
    bool IsSignaled { get; }
    /// <summary>Gets whether the submission armed with this fence has reached the queue, so what it recorded runs or
    /// has run: <see langword="false"/> while an open batch still defers it and once its hand-over failed before the
    /// queue took it (abandoned, until a <see cref="Wait"/> reports that); <see langword="true"/> once it is on the queue,
    /// and when none is outstanding. A caller that acts for a deferred submission after its batch's flush (a shared
    /// signal, a publication) asks its own fence, never whether the flush itself succeeded, since an earlier hand-over
    /// of the same batch may have taken or dropped it. Reading it never blocks and never throws.</summary>
    bool ReachedQueue { get; }

    /// <summary>Blocks until the last submission armed with this fence has retired on the GPU, then re-arms the
    /// fence for reuse. A no-op when no submission is outstanding (never armed, or already waited).</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    /// <exception cref="GpuSubmissionAbandonedException">The submission never reached the queue, so nothing it recorded
    /// ran; the fence is drained for reuse.</exception>
    void Wait();
    /// <summary>Asks for <paramref name="signal"/> to be set once the submission outstanding on this fence has retired,
    /// so a thread asleep on the signal (the host's serviced pacer wait, <c>HostWaits.Pace</c>) wakes for the completion
    /// instead of finding it at its next poll. The signal is set at once when no submission is outstanding, or the
    /// outstanding one has retired, was abandoned or faulted, and it may be set before the submission retires (a lost
    /// device sets it), so whoever wakes reads <see cref="IsSignaled"/>. Asked once for each armed submission, on the
    /// thread that submits; it never blocks.</summary>
    /// <param name="signal">The event to set. It outlives the submission: a backend may hold its native handle until
    /// the submission retires.</param>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> is <see langword="null"/>.</exception>
    void SignalOnCompletion(EventWaitHandle signal);
    /// <summary>Hands the submission armed with this fence to the queue now when an open batch still defers it
    /// (<see cref="IGpuQueueSubmitter.BeginBatch"/>), with every submission deferred before it and the batch kept open,
    /// and does nothing otherwise. A deferred submission neither runs nor sets its completion signal until it reaches the
    /// queue, so a thread that sleeps on that signal while the batch is open hands it off first, or it sleeps behind
    /// its own work. It never blocks on the GPU.</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    void HandOff();
}
