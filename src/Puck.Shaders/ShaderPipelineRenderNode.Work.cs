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
// counts in that pass. The preview and the output finalization are recorded outside every pass.
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
    /// node finds its fence signaled through <see cref="PollReadbacks"/>, at the start of a produced frame, paused frames
    /// included, or waits on it. An install, a resize, a <see cref="Reset"/>, a device loss and disposal withdraw the
    /// sample until a later submission completes.</remarks>
    public bool TryReadCompleted(GpuWorkSample sample) =>
        m_work.TryReadCompleted(sample: sample);
    /// <summary>Reads completed work counters and timestamps without rendering or waiting, and folds the renders found
    /// complete into the summary <see cref="TakeCompletions"/> reads. The render runtime calls this on its frame thread
    /// for an instance whose latest image stands while it <see cref="OwesReadbacks"/>, so its last submission is read
    /// back after its fence signals even when it renders no further frames.</summary>
    public void PollReadbacks() {
        if (m_disposed) { return; }

        m_work.Poll();
        PollTimings();
        FoldCompletions();
    }

    // A successful install, reload or resize withdraws old counters without draining in-flight submissions. Their
    // render completions remain pending independently, and the new passes count under a new revision.
    private void ConfigureWork() {
        m_work.Invalidate();
        m_revision++;
        m_work.Configure(
            passClasses: m_passClasses,
            passLabels: m_passLabels,
            revision: m_revision
        );
    }
    // Records every pass into the frame's list inside its own ledger pass. A package pass that skips the frame
    // (IRenderGraphPackageRecorder.Skips) records nothing and is counted as skipped, never as a pass that ran and did no
    // work. A pass that throws leaves its submission unsealed, so the partial record is dropped rather than published
    // under a pass that never finished. The kernel
    // counters of a graph that counts are cleared ahead of every pass and copied into the slot's readback behind them,
    // outside every pass, and the ledger reads that slot once this submission completes.
    private void RecordPasses(nint command, in FrameContext context, int slot) {
        var passes = m_passes;
        var counters = ((passes.Length > 0)
            ? passes[0].KernelCounters
            : null);

        WriteFrameGroup(slot: slot);

        try {
            PrepareWorkDetails(context: in context, counters: counters, slot: slot);
            counters?.RecordClear(
                commandBuffer: command,
                recorder: m_gpu.Recorder,
                slot: slot
            );
            for (var index = 0; (index < passes.Length); index++) {
                var pass = passes[index];

                if (pass.Package?.Skips(context: in context) == true) {
                    SkipAccesses(
                        pass: pass,
                        slot: slot
                    );
                    SkippedCadence(pass: pass);
                    m_work.SkipPass(pass: index);

                    continue;
                }

                var signature = ((pass.Cadence is { CanStand: true }) ? pass.Package!.Signature(context: in context) : null);

                if (Stands(pass: pass, signature: signature)) {
                    SkipAccesses(pass: pass, slot: slot);
                    m_work.StandPass(pass: index);
                    continue;
                }
                m_work.EnterPass(pass: index);
                PrepareHistoryWrites(pass: pass);
                BeginTiming(command: command, pass: index, slot: slot);
                Record(
                    command: command,
                    context: in context,
                    pass: pass,
                    slot: slot
                );
                EndTiming(command: command, pass: index, slot: slot);
                m_work.LeavePass();
                RecordedCadence(pass: pass, signature: signature);
                pass.Recorded = true;
            }
            if (counters is not null) {
                counters.RecordCopy(
                    commandBuffer: command,
                    recorder: m_gpu.Recorder,
                    slot: slot
                );
                m_work.ReadOnCompletion(
                    readback: counters,
                    slot: slot
                );
            }
        } catch {
            m_work.Invalidate();

            throw;
        }
    }
    private void ResetWork() {
        InvalidateCadence();
        m_work.Invalidate();
        m_resetSubmission = m_submissions;
    }
    // Every node submission goes through here, so m_submissions is the identity the counting submitter just sealed, and
    // a replaced graph waiting for this submission to retire it is armed with its fence. The frame's leased images are
    // sampled by this submission, so the waits they carry are added to it here.
    private void SubmitCounted(List<nint> commands, IGpuSubmissionFence fence) {
        m_frameLeases.AddWaits(submitter: m_gpu.QueueSubmitter);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: CollectionsMarshal.AsSpan(list: commands),
            fence: fence
        );
        m_submissions++;
        m_lastSubmissionFence = fence;
        NoteSubmitted(fence: fence);
        ArmRetirements(fence: fence);
    }
}
