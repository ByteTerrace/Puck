using Puck.Shaders;

namespace Puck.SdfVm;

// One submission's shading identities, held by the residency whose commands carry them from its submission
// (SdfWorldResidency.SubmittedShade). It stays reported in flight until its completion is proven, and only a proof removes it: the node
// waited the slot it was submitted on (SdfShadeSlots.Completed), or its recorder was released after its submissions
// completed and its package began another frame (SdfShadeReleases.BeginFrame). A device loss before either proof marks it
// lost, and a lost submission stays reported until the quarantine reads it. No fence is queried: a reusable slot fence
// reads signaled after an abandoned submission and after a loss's teardown drain alike.
internal sealed class SdfShadeSubmission {
    internal SdfShadeSubmission(SdfWorldResidency residency, IReadOnlyList<string> identities) {
        Residency = residency;
        Identities = identities;
    }

    // The residency that recorded the submission's dispatches, whichever residency its recorder follows afterwards.
    internal SdfWorldResidency Residency { get; }
    internal IReadOnlyList<string> Identities { get; }
    internal bool Lost { get; private set; }

    internal void Complete() {
        if (!Lost) { Residency.RetireShade(submission: this); }
    }
    internal void Lose() => Lost = true;
}

/// <summary>A shading recorder's submissions by frame slot. A recording captures the residency that recorded and the
/// shading identities its dispatches may execute; a submission of that slot moves exactly that capture into the
/// residency's in-flight set (<see cref="SdfWorldResidency.InFlightShadeIdentities"/>); the node's wait of the slot
/// completes it; releasing the recorder hands every unproven submission to its package's
/// <see cref="SdfShadeReleases"/>. Nothing reaches a residency except through a capture its own recording made, so a pass
/// that skipped or stood, or a residency the recorder has since followed away from, never receives another recording's
/// identities. Single-threaded, on the frame thread.</summary>
public sealed class SdfShadeSlots {
    private readonly RenderGraphSlotResults<SdfShadeSubmission> m_slots;

    /// <summary>Initializes a new instance of the <see cref="SdfShadeSlots"/> class.</summary>
    /// <param name="slots">The frames the recorder's graph keeps in flight; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slots"/> is not positive.</exception>
    public SdfShadeSlots(int slots) => m_slots = new RenderGraphSlotResults<SdfShadeSubmission>(slots: slots,
        complete: static submission => submission.Complete(), submit: static submission => submission.Residency.SubmittedShade(submission: submission));

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
        if (plan.Identities.Count == 0) {
            m_slots.Withdraw(slot: slot);
        } else {
            m_slots.Record(slot: slot, result: new SdfShadeSubmission(residency: residency, identities: plan.Identities));
        }
    }
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
