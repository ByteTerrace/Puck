using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// The node's work counters: the per-pass GPU work of its submissions, through IGpuWorkSource, and the GPU objects it
// created, through IWorkCounterSource.
//
// Every GPU service the node holds is wrapped once, in the constructor, by GpuWorkCounting over one ledger, so each
// call is counted where it is made and the node only says which pass it is in. Each pass is entered around its
// recording; the zero initialization recorded at the start of the first pass after an install or reset therefore
// counts in that pass. The float preview and the output finalization are recorded outside every pass.
public sealed partial class ShaderPipelineRenderNode : IGpuWorkSource, IWorkCounterSource {
    private readonly GpuWorkLedger m_work;

    private long m_resetSubmission;
    private long m_revision;
    private long m_submissions;

    /// <summary>Gets the identity of the last submission the node made before its most recent <see cref="Reset"/>, or
    /// zero when it was never reset. The nth submission since that reset has identity
    /// <c>ResetSubmission + n</c>, so a completed sample whose <see cref="GpuWorkSample.Submission"/> reaches it has
    /// counted at least n submissions since the reset.</summary>
    public long ResetSubmission => m_resetSubmission;
    /// <summary>Gets the revision the next submission's pass configuration is counted under: one after the first graph
    /// is installed, increasing by one per install, whether a reload or a resize; zero before any install.</summary>
    public long WorkRevision => m_revision;

    /// <inheritdoc/>
    string IWorkCounterSource.Name =>
        m_work.Name;
    /// <inheritdoc/>
    ReadOnlySpan<WorkKind> IWorkCounterSource.WorkKinds =>
        GpuWork.LifetimeKinds;

    /// <inheritdoc/>
    bool IWorkCounterSource.TryRead(WorkKind kind, out long value) =>
        m_work.TryRead(
            kind: kind,
            value: out value
        );

    /// <inheritdoc/>
    /// <remarks>The passes are the installed graph's, labelled by pass name. A submission becomes available once the
    /// node finds its fence signaled at the start of a produced frame, paused frames included, or waits on it. An
    /// install, a resize, a <see cref="Reset"/>, a device loss and disposal withdraw the sample until a later
    /// submission completes.</remarks>
    public bool TryReadCompleted(GpuWorkSample sample) =>
        m_work.TryReadCompleted(sample: sample);

    // A successful install, reload or resize: submissions still in flight were completed by the install's drain, so
    // nothing recorded under the old graph remains to publish, and the new passes count under a new revision.
    private void ConfigureWork() {
        m_work.Invalidate();
        m_revision++;
        m_work.Configure(
            passLabels: m_passLabels,
            revision: m_revision
        );
    }
    // Records every pass inside its own ledger pass. A pass that throws leaves its submission unsealed, so the partial
    // record is dropped rather than published under a pass that never finished.
    private void RecordPasses(List<nint> commands, in FrameContext context, int slot) {
        var passes = m_passes;

        try {
            for (var index = 0; (index < passes.Length); index++) {
                m_work.EnterPass(pass: index);
                Record(
                    commands: commands,
                    context: in context,
                    pass: passes[index],
                    slot: slot
                );
                m_work.LeavePass();
            }
        } catch {
            m_work.Invalidate();

            throw;
        }
    }
    private void ResetWork() {
        m_work.Invalidate();
        m_resetSubmission = m_submissions;
    }
    // Every node submission goes through here, so m_submissions is the identity the counting submitter just sealed, and
    // a replaced graph waiting for this submission to retire it is armed with its fence.
    private void SubmitCounted(List<nint> commands, IGpuSubmissionFence fence) {
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: CollectionsMarshal.AsSpan(list: commands),
            fence: fence
        );
        m_submissions++;
        m_lastSubmissionFence = fence;
        ArmRetirements(fence: fence);
    }
}
