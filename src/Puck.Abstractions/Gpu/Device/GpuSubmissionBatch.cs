namespace Puck.Abstractions.Gpu;

/// <summary>
/// The submissions one device's queue defers while a batch is open (<see cref="IGpuQueueSubmitter.BeginBatch"/>), handed
/// to the queue together, in their order. The last fenced submission's fence is the batch's owner: the backend arms it
/// once the batch reaches the queue, and every other fenced submission's fence answers from it
/// (<see cref="GpuBatchedSubmissionFence"/>). Both backends' batches are this one class; a backend supplies only how its
/// deferred entries reach the queue and arm the owner.
/// <para>The invariant every backend keeps: anything put on the device's queue follows every submission made before
/// it. A backend enforces it at one door, the read of its native queue (Direct3D 12's
/// <c>DirectXDeviceContext.CommandQueueHandle</c>, Vulkan's <c>VulkanLogicalDevice.GraphicsQueue</c>), which calls
/// <see cref="Split"/>; a wait on a deferred fence splits too. Splitting keeps the batch open, so later submissions are
/// still deferred; only <see cref="Flush"/> closes it.</para>
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
        Split();
    }
    /// <summary>Hands the deferred submissions to the queue and keeps the batch open: whatever reaches the queue next
    /// follows every submission made before it, and later submissions are still deferred. Nothing deferred does
    /// nothing.</summary>
    public abstract void Split();
}

/// <summary>A <see cref="GpuSubmissionBatch"/> whose deferred work is a list of backend entries.</summary>
/// <typeparam name="TEntry">What one deferred submission adds: a command-list token, or a whole submit info.</typeparam>
public abstract class GpuSubmissionBatch<TEntry> : GpuSubmissionBatch {
    private List<TEntry> m_entries = [];
    private List<GpuBatchedSubmissionFence> m_fences = [];
    // The lists a hand-off is submitting, swapped out first so a queue door read during the submit finds nothing.
    private List<TEntry> m_handing = [];
    private List<GpuBatchedSubmissionFence> m_handingFences = [];

    /// <summary>Defers a submission when the batch is open and the submission is not empty.</summary>
    /// <param name="entries">The submission's entries.</param>
    /// <param name="fence">The fence it arms, or <see langword="null"/>; it must have no submission outstanding.</param>
    /// <returns><see langword="true"/> when the submission was deferred; otherwise the caller submits it now.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="fence"/> has a submission outstanding.</exception>
    public bool TryDefer(ReadOnlySpan<TEntry> entries, GpuBatchedSubmissionFence? fence) {
        if (!IsOpen || entries.IsEmpty) { return false; }

        fence?.Defer(batch: this);
        foreach (var entry in entries) { m_entries.Add(item: entry); }
        if (fence is not null) { m_fences.Add(item: fence); }

        return true;
    }
    /// <inheritdoc/>
    public sealed override void Split() {
        if (m_entries.Count == 0) { return; }

        var entries = m_entries;
        var fences = m_fences;
        var owner = ((fences.Count == 0) ? null : fences[^1]);

        (m_entries, m_handing) = (m_handing, entries);
        (m_fences, m_handingFences) = (m_handingFences, fences);
        try {
            Submit(entries: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: entries), owner: owner);
        } finally {
            // Even after a failed submit every fence answers from the owner. An owner the backend never armed never
            // reached the queue, so it and every fence answering from it have nothing outstanding; one it armed reports
            // its device's loss through its own native fence.
            owner?.AbandonIfDeferred();
            foreach (var fence in fences) {
                if (!ReferenceEquals(objA: fence, objB: owner)) { fence.Alias(owner: owner!); }
            }
            entries.Clear();
            fences.Clear();
        }
    }
    /// <summary>Puts the deferred entries on the queue in their order and arms <paramref name="owner"/>
    /// (<see cref="GpuBatchedSubmissionFence.MarkArmed"/>) once its signal is on the queue behind them. Reads the
    /// backend's native queue without passing its door, since these entries are what the door hands over.</summary>
    /// <param name="entries">The deferred entries, in their order; never empty.</param>
    /// <param name="owner">The last fenced submission's fence, or <see langword="null"/>.</param>
    protected abstract void Submit(ReadOnlySpan<TEntry> entries, GpuBatchedSubmissionFence? owner);
}
