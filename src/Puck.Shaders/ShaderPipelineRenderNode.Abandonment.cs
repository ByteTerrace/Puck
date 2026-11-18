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
public sealed partial class ShaderPipelineRenderNode {
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

        m_slots[slot].ReadbacksOwed = false;
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
