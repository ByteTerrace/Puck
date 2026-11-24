using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// A slot whose submission never reached the queue: its batch's hand-off failed, and its fence reports
// GpuSubmissionAbandonedException once, from IsSignaled until a wait drains it. Nothing it recorded ran, so nothing keyed
// to it is proven: its readbacks are dropped, never completed, its render and its timestamps are never folded, and the
// first-submission watch does not count it. Nor does it prove the queue passed the node's earlier submissions, which
// the node's other retirements (replaced graphs, published images, leases) were waiting on; so the node settles every
// other slot through WaitSlot, after which nothing it submitted is in flight and a drained fence reading signaled is true.
// Every read of one of the node's fences goes through Signaled or WaitSlot, so a drained abandoned fence never proves the
// submission it carried.
//
// A frame whose submission an open batch defers commits its history, initialization, publication and export before
// anything reaches the queue, so those commits stand only once that submission's own hand-over took it. The one door
// that decides is SettleHandOver: the batch's caller settles after its flush, the node settles at the start of its next
// frame and before installing a graph, and an abandoned slot settles when its wait reports the abandonment. A submission
// that reached the queue keeps the commits and completes its export; one that did not rolls the frame back to its
// recording checkpoint (history cursors, instance states, content versions, initialization), withdraws its publication
// and ends its export unwritten.
//
// A package's own commits (IRenderGraphPackageRecorder.Submitted: a camera a layer was drawn from, a target known clear)
// have no checkpoint to roll back to, so they wait for the hand-over instead: CommitPackages is the one place that calls
// Submitted, and it runs only for a submission that reached the queue, at once when it went over unbatched, or from
// SettleAs once its batch's hand-over took it. A recording whose submission never reached the queue commits nothing.
public sealed partial class ShaderPipelineRenderNode {
    // The fence of the latest frame whose submission an open batch still deferred when it committed, or null once its
    // hand-over is settled.
    private IGpuSubmissionFence? m_handOverPending;
    // The recorders of the latest frame's recorded passes, owed Submitted until its submission reaches the queue.
    private readonly List<IRenderGraphPackageRecorder> m_commitsOwed = [];

    // Owes the frame's recorded passes their commit, and pays it at once when the submission already reached the queue.
    // Called once the frame's cadence is committed (CommitCadenceFrame), which decides whether its hand-over is pending.
    private void OwePackageCommits() {
        m_commitsOwed.Clear();
        foreach (var pass in m_passes) {
            if (pass.Recorded && (pass.Package is { } package)) { m_commitsOwed.Add(item: package); }
        }
        if (m_handOverPending is null) { CommitPackages(); }
    }
    private void CommitPackages() {
        try {
            foreach (var package in m_commitsOwed) { package.Submitted(); }
        } finally {
            m_commitsOwed.Clear();
        }
    }

    /// <summary>Gets whether the latest frame's hand-over is still to be settled (<see cref="SettleHandOver"/>): its
    /// submission was deferred in an open batch, or its export's completion was left to the caller.</summary>
    internal bool OwesSettlement => ((m_handOverPending is not null) || (m_exportOwed is not null));

    /// <summary>Settles the latest frame against its submission's hand-over, once the caller has flushed the batch the
    /// submission joined: a submission that reached the queue keeps the frame's commits and completes its export for the
    /// reader; one that never reached the queue (its hand-over failed, in the flush or in an earlier hand-over of the same
    /// batch) rolls the frame's history and initialization back to its recording checkpoint, withdraws its publication
    /// and ends its export unwritten. A submission an open batch still defers is handed to the queue first. Does nothing
    /// when nothing is owed.</summary>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    internal void SettleHandOver() {
        if (m_handOverPending is { } fence) {
            try {
                fence.HandOff();
            } catch {
                SettleAs(reached: fence.ReachedQueue);

                throw;
            }
        }
        SettleAs(reached: (m_handOverPending?.ReachedQueue ?? true));
    }
    private void SettleAs(bool reached) {
        var pending = (m_handOverPending is not null);

        m_handOverPending = null;
        try {
            if (pending && reached) { CommitPackages(); }
            if (pending && !reached) { m_commitsOwed.Clear(); RollBackHandOver(); }
        } finally {
            EndOwedExport(reached: reached);
        }
    }
    // Undoes what the frame committed for a submission that never ran: the checkpoint puts the history cursors, instance
    // states, content versions and initialization back, the frame counter steps back with the cursors (a graphics history
    // writer's framebuffer ring is indexed by it), and the image and buffers the frame published are withdrawn.
    private void RollBackHandOver() {
        RestoreCadenceCheckpoint();
        m_frame--;
        Unpublish();
        m_publicationLost = true;
        PublicationAbandoned?.Invoke(this);
    }
    internal Action<ShaderPipelineRenderNode>? PublicationAbandoned { get; set; }
    // Whether the fence's submission has completed, settling an abandoned slot of this node first. An abandoned fence the
    // node does not own (another node's latest submission a borrowed publication waits on) reads not signaled, and is left
    // for its owner to drain.
    private bool Signaled(IGpuSubmissionFence fence) {
        try {
            return fence.IsSignaled;
        } catch (GpuSubmissionAbandonedException) {
            var slot = SlotOf(fence: fence);

            if (slot < 0) {
                return false;
            }

            WaitSlot(slot: slot);

            return true;
        }
    }
    // Drops what the slot's abandoned submission carried, then settles every other slot. Called only from WaitSlot, whose
    // wait reported the abandonment and drained the fence.
    private void AbandonSlot(int slot) {
        var fence = m_slots[slot].Fence!;

        if (ReferenceEquals(objA: m_handOverPending, objB: fence)) { SettleAs(reached: false); }

        m_slots[slot].ReadbacksOwed = false;
        m_slots[slot].Settled = m_slots[slot].Submission;
        foreach (var pass in m_passes) {
            if (pass?.Package is IRenderGraphPackageReadback readback) {
                readback.Abandoned(slot: slot);
            }
        }
        for (var index = 0; (index < m_pendingRenders.Length); index++) {
            if (ReferenceEquals(objA: m_pendingRenders[index].Fence, objB: fence)) {
                m_pendingRenders[index] = default;
            }
        }
        if ((slot < m_timingSlots.Length) && (m_timingSlots[slot] is { } timing) && ReferenceEquals(objA: timing.Fence, objB: fence)) {
            timing.Fence = null;
        }
        if (ReferenceEquals(objA: m_firstSubmission, objB: fence)) {
            m_firstSubmission = null;
        }
        for (var other = 0; (other < m_slots.Length); other++) {
            if ((other != slot) && (m_slots[other].Fence is not null)) {
                WaitSlot(slot: other);
            }
        }
        // The abandoned submission did not execute, and every other slot has now been waited. This settles
        // resource use, without certifying the abandoned render or its readbacks as completed.
        m_completedSubmissions = Math.Max(m_completedSubmissions, m_slots[slot].Submission);
    }
    private int SlotOf(IGpuSubmissionFence fence) {
        for (var slot = 0; (slot < m_slots.Length); slot++) {
            if (ReferenceEquals(objA: m_slots[slot].Fence, objB: fence)) {
                return slot;
            }
        }
        return -1;
    }

    /// <summary>Gets whether the node's latest submission may still be running: its fence has not signaled. An abandoned
    /// latest submission settles the node first (every other slot waited), after which nothing is running.</summary>
    internal bool LatestSubmissionRunning => ((m_lastSubmissionFence is { } fence) && !Signaled(fence: fence));
}
