namespace Puck.Commands;

/// <summary>
/// The per-slot modifier state machine behind <see cref="PagedInputBindings"/>: latches each declared modifier
/// held/released with hysteresis and keeps the held set in press order, so an ordered chord
/// (<c>left</c>-then-<c>right</c> vs <c>right</c>-then-<c>left</c>) selects distinct pages. Determinism comes
/// for free — the <see cref="InputRouter"/> applies signals in <c>(CaptureTick, Sequence)</c> order on a single
/// thread, and this state is a pure function of that sequence. A thin, signal-resolving wrapper over the shared
/// <see cref="HeldOrderTracker"/> primitive, sized to <see cref="CompiledBindingProfile.Modifiers"/>' per-modifier
/// thresholds.
/// </summary>
public sealed class BindingChordTracker {
    private readonly CompiledBindingProfile m_profile;
    private readonly HeldOrderTracker m_tracker;

    /// <summary>Initializes a new instance of the <see cref="BindingChordTracker"/> class.</summary>
    /// <param name="profile">The compiled profile whose modifiers are tracked.</param>
    public BindingChordTracker(CompiledBindingProfile profile) {
        m_profile = profile;
        m_tracker = new HeldOrderTracker(
            pressThresholds: [.. profile.Modifiers.Select(selector: static modifier => modifier.PressThreshold)],
            releaseThresholds: [.. profile.Modifiers.Select(selector: static modifier => modifier.ReleaseThreshold)]
        );
    }

    /// <summary>Gets the active page index for the currently held, ordered modifier set.</summary>
    public int ActivePageIndex => m_profile.PageIndexOf(heldOrder: m_tracker.HeldOrder);

    /// <summary>Applies a signal to the tracker.</summary>
    /// <param name="signal">The signal, in the router's deterministic capture order.</param>
    /// <returns><see langword="true"/> when the signal drove a declared modifier (the active page may have changed).</returns>
    public bool Apply(in InputSignal signal) {
        if (!m_profile.TryGetModifier(
            source: signal.Source,
            modifierIndex: out var modifierIndex
        )) {
            return false;
        }

        // The X component covers both shapes a modifier arrives in: an analog trigger's Axis1D magnitude and a
        // digital button's 0/1. A release/cancel edge always releases, whatever value it carries.
        var released = (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled);
        var value = (released
            ? 0f
            : signal.Value.AsAxis1D);

        _ = m_tracker.Set(index: modifierIndex, value: value);

        return true;
    }

    /// <summary>Releases every modifier (focus loss, device disconnect, or a profile reload).</summary>
    public void Reset() => m_tracker.Reset();
}
