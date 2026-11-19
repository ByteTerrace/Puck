namespace Puck.Abstractions.Gpu;

/// <summary>
/// The submissions one device's queue defers while a batch is open (<see cref="IGpuQueueSubmitter.BeginBatch"/>), handed
/// to the queue together, in their order. The last fenced submission of a hand-over owns its fence: the backend arms it
/// once the entries reach the queue, and every other fenced submission's fence in that hand-over answers from it
/// (<see cref="GpuBatchedSubmissionFence"/>). Both backends' batches are this one class; a backend supplies only how its
/// deferred entries reach the queue and arm the owner.
/// <para>The invariant every backend keeps: anything put on the device's queue follows every submission made before
/// it. A backend enforces it at one door, the read of its native queue (Direct3D 12's
/// <c>DirectXDeviceContext.CommandQueueHandle</c>, Vulkan's <c>VulkanLogicalDevice.GraphicsQueue</c>), which calls
/// <see cref="Split"/>; a wait on a deferred fence splits too. Splitting keeps the batch open, so later submissions are
/// still deferred; only <see cref="Flush"/> closes it.</para>
/// <para>What a fence proves follows from it: a fence answers from a signal queued after its own entries, so once it
/// reads signaled its submission and every submission made before it have completed. It proves nothing about a
/// submission made after it, even one deferred in the same batch. A background job's fenced submission
/// (<see cref="GpuQueueWork.IsBackground"/>) is answered from its own hand-over's signal, never from a later frame's
/// work: work that is not a background job's, deferred behind one, first hands the batch to the queue
/// (<see cref="GpuSubmissionBatch{TEntry}.Admit"/>).</para>
/// <para>Every hand-off counts in <see cref="GpuQueueWork"/>: its execute under the cause the split names and the
/// category of the code that split it, and each list under the category it was deferred with. The backend counts the
/// owner's signal at its native call.</para>
/// Single-threaded, like every pump-thread GPU object.
/// </summary>
public abstract class GpuSubmissionBatch {
    private protected GpuSubmissionBatch() { }

    /// <summary>Gets whether a batch is open: a submission made now is deferred.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Opens the batch; opening an open batch keeps it open.</summary>
    public void Begin() => IsOpen = true;
    /// <summary>Hands the deferred submissions to the queue and closes the batch.</summary>
    public void Flush() {
        IsOpen = false;
        Split(cause: GpuQueueCause.BatchEnd);
    }
    /// <summary>Hands the deferred submissions to the queue and keeps the batch open: whatever reaches the queue next
    /// follows every submission made before it, and later submissions are still deferred. Nothing deferred does
    /// nothing.</summary>
    /// <param name="cause">The reason the deferred work reaches the queue now, which the hand-off's execute counts
    /// under.</param>
    public abstract void Split(GpuQueueCause cause);
}

/// <summary>A <see cref="GpuSubmissionBatch"/> whose deferred work is a list of backend entries.</summary>
/// <typeparam name="TEntry">What one deferred submission adds: a command-list token, or a whole submit info.</typeparam>
public abstract class GpuSubmissionBatch<TEntry> : GpuSubmissionBatch {
    private List<TEntry> m_entries = [];
    private List<GpuQueueCategory> m_owners = [];
    private List<GpuBatchedSubmissionFence> m_fences = [];
    // The lists a hand-off is submitting, swapped out first so a queue door read during the submit finds nothing.
    private List<TEntry> m_handing = [];
    private List<GpuQueueCategory> m_handingOwners = [];
    private List<GpuBatchedSubmissionFence> m_handingFences = [];
    // The background jobs' fences deferred since the last hand-over, and how many of them have since had work that is
    // not a background job's deferred behind them. Admit hands the batch over before such work, so the second stays zero
    // unless a derived batch defers past Admit; Split counts both (GpuQueueWork.Jobs).
    private int m_jobs;
    private int m_lateJobs;

    /// <summary>Readies the batch to defer a submission made now under the calling thread's
    /// <see cref="GpuQueueWork.Current"/> category. A submission that is not a background job's
    /// (<see cref="GpuQueueWork.IsBackground"/>), made while a background job's fenced submission is deferred, first hands
    /// the deferred work to the queue (<see cref="GpuQueueCause.JobEnd"/>), so the job's fence answers from that
    /// hand-over's signal rather than from the work deferred after it. <see cref="TryDefer"/> calls it; a derived batch
    /// that copies a submission into its own lists calls it before copying, since the hand-over it may make submits and
    /// clears those lists. A hand-over that fails rethrows its failure and leaves <paramref name="fence"/> abandoned
    /// (<see cref="GpuBatchedSubmissionFence.MarkAbandoned"/>), since nothing of its submission reached the queue.</summary>
    /// <param name="fence">The fence the submission arms, or <see langword="null"/>; it must have no submission
    /// outstanding.</param>
    /// <returns><see langword="true"/> when the batch is open and the submission is to be deferred; otherwise the caller
    /// submits it now.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="fence"/> has a submission outstanding.</exception>
    protected bool Admit(GpuBatchedSubmissionFence? fence) {
        if (!IsOpen) { return false; }

        fence?.RequireDrained();
        if ((m_jobs != m_lateJobs) && !GpuQueueWork.IsBackground(category: GpuQueueWork.Current)) {
            try {
                Split(cause: GpuQueueCause.JobEnd);
            } catch {
                fence?.MarkAbandoned();

                throw;
            }
        }

        return true;
    }
    /// <summary>Defers a submission when the batch is open and the submission is not empty, its lists counted under the
    /// calling thread's <see cref="GpuQueueWork.Current"/> category, after any hand-over <see cref="Admit"/> makes (whose
    /// failure it rethrows with <paramref name="fence"/> abandoned).</summary>
    /// <param name="entries">The submission's entries.</param>
    /// <param name="lists">The number of command lists the entries carry.</param>
    /// <param name="fence">The fence it arms, or <see langword="null"/>; it must have no submission outstanding.</param>
    /// <returns><see langword="true"/> when the submission was deferred; otherwise the caller submits it now.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="fence"/> has a submission outstanding.</exception>
    public bool TryDefer(ReadOnlySpan<TEntry> entries, int lists, GpuBatchedSubmissionFence? fence) {
        if (entries.IsEmpty || !Admit(fence: fence)) { return false; }

        var category = GpuQueueWork.Current;
        var background = GpuQueueWork.IsBackground(category: category);

        fence?.Defer(batch: this);
        foreach (var entry in entries) { m_entries.Add(item: entry); }
        for (var list = 0; (list < lists); list++) { m_owners.Add(item: category); }
        if (fence is not null) {
            m_fences.Add(item: fence);
            if (background) { m_jobs++; }
        }
        if (!background) { m_lateJobs = m_jobs; }

        return true;
    }
    /// <inheritdoc/>
    public sealed override void Split(GpuQueueCause cause) {
        if (m_entries.Count == 0) { return; }

        var entries = m_entries;
        var owners = m_owners;
        var fences = m_fences;
        var owner = ((fences.Count == 0) ? null : fences[^1]);
        var jobs = m_jobs;
        var lateJobs = m_lateJobs;

        (m_entries, m_handing) = (m_handing, entries);
        (m_owners, m_handingOwners) = (m_handingOwners, owners);
        (m_fences, m_handingFences) = (m_handingFences, fences);
        m_jobs = 0;
        m_lateJobs = 0;
        try {
            GpuQueueWork.Process.Execute(cause: cause, owners: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: owners));
            GpuQueueWork.Process.Jobs(handed: jobs, late: lateJobs);
            if (cause == GpuQueueCause.BatchEnd) { GpuQueueWork.Process.Batch(); }
            Submit(entries: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: entries), owner: owner);
        } finally {
            // An owner the backend left deferred never reached the queue: the backend arms it (or marks it run, or
            // faulted) once its entries are on the queue. Its submissions then ran nowhere, so every fence of the batch
            // is abandoned, a state each reports rather than reading complete. Otherwise every fence answers from the
            // owner, which reports its device's loss or its fault itself.
            if (owner is { IsDeferred: true }) {
                foreach (var fence in fences) { fence.MarkAbandoned(); }
            } else {
                foreach (var fence in fences) {
                    if (!ReferenceEquals(objA: fence, objB: owner)) { fence.Alias(owner: owner!); }
                }
            }
            entries.Clear();
            owners.Clear();
            fences.Clear();
        }
    }
    /// <summary>Puts the deferred entries on the queue in their order and arms <paramref name="owner"/>
    /// (<see cref="GpuBatchedSubmissionFence.MarkArmed"/>) once its signal is on the queue behind them. Reads the
    /// backend's native queue without passing its door, since these entries are what the door hands over. The base class
    /// has already counted the execute; the backend counts the owner's signal.</summary>
    /// <param name="entries">The deferred entries, in their order; never empty.</param>
    /// <param name="owner">The last fenced submission's fence, or <see langword="null"/>.</param>
    protected abstract void Submit(ReadOnlySpan<TEntry> entries, GpuBatchedSubmissionFence? owner);
}
