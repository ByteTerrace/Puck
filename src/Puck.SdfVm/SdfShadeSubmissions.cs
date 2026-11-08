using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// One submission's shading identities, held by the residency whose commands carry them from its submission
// (SdfWorldResidency.SubmittedShade), and the specialized kernel set its dispatches recorded with, which it pins. It stays
// reported in flight until its completion is proven, and only a proof removes it: the node waited the slot it was
// submitted on (SdfShadeSlots.Completed), or its recorder was released after its submissions completed and its package
// began another frame (SdfShadeReleases.BeginFrame). A device loss before either proof marks it lost, and a lost
// submission stays reported until the quarantine reads it. No fence is queried: a reusable slot fence reads signaled
// after an abandoned submission and after a loss's teardown drain alike. The pin drops on the proof, on a loss, or when
// the recording is discarded before any submission; the residency frees a retired set only at zero pins.
internal sealed class SdfShadeSubmission {
    private SdfShadePipelines? m_pipelines;

    internal SdfShadeSubmission(SdfWorldResidency residency, IReadOnlyList<string> identities, SdfShadePipelines? pipelines = null) {
        Residency = residency;
        Identities = identities;
        m_pipelines = pipelines;
        m_pipelines?.Pin();
    }

    // The residency that recorded the submission's dispatches, whichever residency its recorder follows afterwards.
    internal SdfWorldResidency Residency { get; }
    internal IReadOnlyList<string> Identities { get; }
    internal bool Lost { get; private set; }

    internal void Complete() {
        Unpin();
        if (!Lost) { Residency.RetireShade(submission: this); }
    }
    internal void Lose() {
        Lost = true;
        Unpin();
    }
    // A recording that never reached a submission: nothing of it is in flight.
    internal void Discard() => Unpin();

    private void Unpin() {
        m_pipelines?.Unpin();
        m_pipelines = null;
    }
}

/// <summary>A shading recorder's submissions by frame slot. A recording captures the residency that recorded, the
/// shading identities its dispatches may execute and the specialized kernels it recorded with; a submission of that slot
/// moves exactly that capture into the residency's in-flight set (<see cref="SdfWorldResidency.InFlightShadeIdentities"/>);
/// the node's wait of the slot completes it; releasing the recorder hands every unproven submission to its package's
/// <see cref="SdfShadeReleases"/>. Nothing reaches a residency except through a capture its own recording made, so a pass
/// that skipped or stood, or a residency the recorder has since followed away from, never receives another recording's
/// identities. Single-threaded, on the frame thread.</summary>
public sealed class SdfShadeSlots {
    private readonly RenderGraphSlotResults<SdfShadeSubmission> m_slots;

    /// <summary>Initializes a new instance of the <see cref="SdfShadeSlots"/> class.</summary>
    /// <param name="slots">The frames the recorder's graph keeps in flight; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slots"/> is not positive.</exception>
    public SdfShadeSlots(int slots) => m_slots = new RenderGraphSlotResults<SdfShadeSubmission>(slots: slots,
        complete: static submission => submission.Complete(), discard: static submission => submission.Discard(),
        submit: static submission => submission.Residency.SubmittedShade(submission: submission));

    /// <summary>Captures what a recording into the slot dispatched: the residency that recorded it and the identities its
    /// plan may execute. It replaces any earlier capture of the slot that never reached a submission; a plan that may
    /// execute no identity captures nothing.</summary>
    /// <param name="slot">The frame slot recorded into.</param>
    /// <param name="residency">The residency whose tables the dispatches bind.</param>
    /// <param name="plan">The recorded plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="residency"/> or <paramref name="plan"/> is null.</exception>
    public void Recorded(int slot, SdfWorldResidency residency, SdfShadeDispatch plan) {
        ArgumentNullException.ThrowIfNull(residency);
        ArgumentNullException.ThrowIfNull(plan);
        Capture(pipelines: null, plan: plan, residency: residency, slot: slot);
    }
    // The one door through which a views or glass recording takes its kernels: the residency's shading selection, pinned
    // by the capture before any native handle reaches the recorder, so the pin travels with the submission even if a later
    // frame publishes another set or this recorder follows another residency. The selection answers every kernel of the
    // recording from that one capture.
    internal SdfShadeSelection RecordSelection(int slot, SdfWorldResidency residency, SdfShadeDispatch plan) {
        ArgumentNullException.ThrowIfNull(residency);
        ArgumentNullException.ThrowIfNull(plan);
        var pipelines = residency.ShadeSelection;

        Capture(pipelines: pipelines, plan: plan, residency: residency, slot: slot);
        return new SdfShadeSelection(pipelines: pipelines, residency: residency);
    }
    // A recording of one shading kernel through the door above.
    internal IGpuComputePipeline RecordPipeline(int slot, SdfWorldResidency residency, SdfShadeDispatch plan, SdfKernel kernel) =>
        RecordSelection(plan: plan, residency: residency, slot: slot).Pipeline(kernel: kernel);
    /// <summary>Reports the slot's recording submitted: its capture enters the recording residency's in-flight set. A slot
    /// with no capture since its last submission submits nothing.</summary>
    /// <param name="slot">The submitted frame slot.</param>
    public void Submitted(int slot) => m_slots.Submitted(slot: slot);
    /// <summary>Reports the slot's previous submission completed: its identities leave the residency that recorded them,
    /// and a capture that never reached a submission is dropped.</summary>
    /// <param name="slot">The frame slot whose previous submission completed.</param>
    public void Completed(int slot) => m_slots.Completed(slot: slot);
    /// <summary>Hands every submission not yet proven complete to the package's releases, as the recorder is released.</summary>
    /// <param name="releases">The releases of the recorder's package.</param>
    /// <exception cref="ArgumentNullException"><paramref name="releases"/> is null.</exception>
    public void Release(SdfShadeReleases releases) {
        ArgumentNullException.ThrowIfNull(releases);
        m_slots.Release(unproven: releases.Add);
    }

    private void Capture(int slot, SdfWorldResidency residency, SdfShadeDispatch plan, SdfShadePipelines? pipelines) {
        if ((plan.Identities.Count == 0) && (pipelines is null)) {
            m_slots.Withdraw(slot: slot);
        } else {
            m_slots.Record(slot: slot, result: new SdfShadeSubmission(identities: plan.Identities, pipelines: pipelines, residency: residency));
        }
    }
}

// The kernels one recording shades with, as SdfShadeSlots.RecordSelection captured them: a published set's
// specialization of a kernel, or with none the residency's fixed kernel (the interpreter, or the plain one for a frame
// with no active program); and a kernel's plain counterpart, which shades no program whatever the selection. Valid only
// for the recording that captured it, whose capture pins the set.
internal readonly struct SdfShadeSelection(SdfShadePipelines? pipelines, SdfWorldResidency residency) {
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => (pipelines?.Pipeline(kernel: kernel) ?? residency.ShadeFallback(kernel: kernel));
    internal IGpuComputePipeline Plain(SdfKernel kernel) => residency.ShadePlain(kernel: kernel);
}

/// <summary>The shading submissions of a package's released recorders. A render node releases a recorder only after the
/// submissions that recorded it completed, except on a device loss, where it releases them without waiting; so the
/// package's next frame completes what was released before it, and a loss first marks everything released since the last
/// frame lost, together with whatever its own teardown releases before the next frame. A lost submission stays in its
/// residency's <see cref="SdfWorldResidency.InFlightShadeIdentities"/>. Single-threaded, on the frame thread.</summary>
public sealed class SdfShadeReleases {
    private readonly List<SdfShadeSubmission> m_released = [];
    private bool m_lost;

    /// <summary>Completes every submission released since the previous frame began, and ends a device loss's
    /// teardown.</summary>
    public void BeginFrame() {
        foreach (var submission in m_released) { submission.Complete(); }
        m_released.Clear();
        m_lost = false;
    }
    /// <summary>Marks every submission released since the previous frame began lost, and every one released before the
    /// next frame begins.</summary>
    public void DeviceLost() {
        foreach (var submission in m_released) { submission.Lose(); }
        m_released.Clear();
        m_lost = true;
    }

    internal void Add(SdfShadeSubmission submission) {
        if (m_lost) { submission.Lose(); } else { m_released.Add(item: submission); }
    }
}
