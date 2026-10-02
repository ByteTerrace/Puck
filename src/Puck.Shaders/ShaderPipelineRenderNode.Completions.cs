using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// Render completions outlive a work revision: an install withdraws counters without draining the GPU. Keep each render's
// fence and grid until it completes or its device is lost, independently of the work ledger's invalidation.
public sealed partial class ShaderPipelineRenderNode {
    private ShaderPipelineCompletions m_completions;
    private (long Submission, double Grid, IGpuSubmissionFence? Fence)[] m_pendingRenders = [];

    /// <summary>Gets whether a submission's work counters or timestamps are still to be read back: a sealed submission
    /// waits on its fence, a render awaits completion across an install, or a timed frame slot holds a fence or a pool to
    /// retire. A node that owes nothing has nothing for <see cref="PollReadbacks"/> to read.</summary>
    public bool OwesReadbacks => (!m_disposed && (m_work.HasPending || RendersPending || TimingPending));

    /// <summary>Reads and clears the summary of the renders this node found complete since the previous read: how many,
    /// and the one grid every one of them recorded, or zero when they recorded different grids. Every render in the span
    /// counts, not only the newest, so a render at another grid between two reads is never hidden by a later one. A
    /// submission that rendered nothing (a republished image) is no render. An install or reset keeps completed renders
    /// in the summary; device loss drops renders whose completion was never observed.</summary>
    /// <returns>The completions since the previous read.</returns>
    public ShaderPipelineCompletions TakeCompletions() {
        FoldCompletions();

        var completions = m_completions;

        m_completions = default;

        return completions;
    }

    private bool RendersPending {
        get {
            foreach (var render in m_pendingRenders) {
                if (render.Fence is not null) { return true; }
            }
            return false;
        }
    }

    private void NoteRenderCompletion(int slot, IGpuSubmissionFence fence) {
        if (m_pendingRenders.Length == 0) {
            m_pendingRenders = new (long, double, IGpuSubmissionFence?)[m_inFlight];
        }
        m_pendingRenders[slot] = (m_submissions, m_renderGrid, fence);
    }
    private void FoldCompletions() {
        foreach (var render in m_pendingRenders) {
            if ((render.Fence is { } fence) && fence.IsSignaled) {
                CompleteRenders(submission: render.Submission);
            }
        }
    }
    // A waited fence is about to be reused, so fold its render before it is rearmed. One queue completes every older
    // submission too, including those whose work records an install invalidated.
    private void CompleteRenderFence(IGpuSubmissionFence fence) {
        foreach (var render in m_pendingRenders) {
            if (ReferenceEquals(objA: render.Fence, objB: fence)) {
                CompleteRenders(submission: render.Submission);
                return;
            }
        }
    }
    private void CompleteRenders(long submission) {
        for (var index = 0; (index < m_pendingRenders.Length); index++) {
            var render = m_pendingRenders[index];

            if ((render.Fence is not null) && (render.Submission <= submission)) {
                m_completions = m_completions.Then(later: new ShaderPipelineCompletions(Grid: render.Grid, Renders: 1));
                m_pendingRenders[index] = default;
            }
        }
    }
}
