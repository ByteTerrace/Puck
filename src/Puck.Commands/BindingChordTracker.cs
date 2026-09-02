namespace Puck.Commands;

/// <summary>
/// The per-slot modifier state machine behind <see cref="PagedInputBindings"/>: latches each declared modifier
/// held/released with hysteresis and keeps the held set in press order, so an ordered chord
/// (<c>left</c>-then-<c>right</c> vs <c>right</c>-then-<c>left</c>) resolves distinct rows. Determinism comes
/// for free — the <see cref="InputRouter"/> applies signals in <c>(CaptureTick, Sequence)</c> order on a single
/// thread, and this state is a pure function of that sequence. A signal-resolving wrapper over the shared
/// <see cref="HeldOrderTracker"/> primitive, sized to <see cref="CompiledBindingProfile.Modifiers"/>' per-modifier
/// thresholds. Row resolution (group-scoped, prefix-deep) lives on <see cref="CompiledBindingProfile"/>; this
/// type owns only the held-order truth.
/// </summary>
/// <remarks>A modifier with several declared sources is held while ANY of them is down: this type keeps its own
/// per-source down set for each modifier and drives <see cref="HeldOrderTracker"/> with a synthetic all-or-nothing
/// value on the aggregate's 0↔1 transitions only — the FIRST source to cross press joins the held order (so a
/// chord's press order is the modifier's first source press), and the LAST down source to release leaves it.</remarks>
public sealed class BindingChordTracker {
    private readonly HashSet<string>[] m_downSources;
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
        m_downSources = new HashSet<string>[profile.Modifiers.Count];

        for (var index = 0; (index < m_downSources.Length); index++) {
            m_downSources[index] = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Gets the held modifier indices, in press order.</summary>
    public ReadOnlySpan<int> HeldOrder => m_tracker.HeldOrder;

    /// <summary>Applies a signal to the tracker.</summary>
    /// <param name="signal">The signal, in the router's deterministic capture order.</param>
    /// <returns><see langword="true"/> when the signal changed the held modifier order.</returns>
    public bool Apply(in InputSignal signal) {
        if (!m_profile.TryGetModifier(
            source: signal.Source,
            modifierIndex: out var modifierIndex
        )) {
            return false;
        }

        var modifier = m_profile.Modifiers[modifierIndex];
        var value = ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled)
            ? 0f
            : signal.Value.AsAxis1D
        );
        var down = m_downSources[modifierIndex];

        if (
            !down.Contains(item: signal.Source) &&
            (value >= modifier.PressThreshold)
        ) {
            _ = down.Add(item: signal.Source);

            return (
                (down.Count == 1) &&
                m_tracker.Set(
                index: modifierIndex,
                value: 1f
            )
            );
        }

        if (
            down.Contains(item: signal.Source) &&
            (value <= modifier.ReleaseThreshold)
        ) {
            _ = down.Remove(item: signal.Source);

            return (
                (down.Count == 0) &&
                m_tracker.Set(
                index: modifierIndex,
                value: 0f
            )
            );
        }

        return false;
    }
    /// <summary>Returns whether one source is currently latched down as part of the modifier it names.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <returns><see langword="true"/> when the source names a declared modifier and is one of the sources currently
    /// holding it down. Per SOURCE, not per modifier: a modifier with several declared sources stays held while any
    /// of them is down, and the question here is whether THIS source's release has something to give up.</returns>
    public bool IsDown(string source) {
        return (
            m_profile.TryGetModifier(
            source: source,
            modifierIndex: out var modifierIndex
        ) &&
            m_downSources[modifierIndex].Contains(item: source)
        );
    }
    /// <summary>Releases every modifier (focus loss, device disconnect, or a profile reload).</summary>
    public void Reset() {
        m_tracker.Reset();

        foreach (var down in m_downSources) {
            down.Clear();
        }
    }
}
