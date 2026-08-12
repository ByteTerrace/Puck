namespace Puck.Commands;

/// <summary>The transition a <see cref="RowActivatorTracker"/> reports for one applied signal.</summary>
public enum RowActivatorTransition {
    /// <summary>No change — the signal was irrelevant, or advanced/reset progress without opening or completing.</summary>
    None,

    /// <summary>A <see cref="BindingActivatorMode.Held"/> sequence just became fully held, in order (fire the
    /// entry's press).</summary>
    Opened,

    /// <summary>A previously-open <see cref="BindingActivatorMode.Held"/> sequence just broke (fire the entry's
    /// release).</summary>
    Closed,

    /// <summary>A <see cref="BindingActivatorMode.Tapped"/> sequence was just completed (fire the entry once).</summary>
    Completed,
}

/// <summary>
/// The per-row, per-slot state machine behind a <see cref="BindingActivatorDefinition"/> — one instance per
/// (compiled activator entry, slot), independent of <see cref="BindingChordTracker"/>'s shared per-group modifier
/// tracker: an activator's sequence names arbitrary physical controls, not declared page/group modifiers, so each
/// row needs its own held-order or tap-progress state rather than sharing one profile-wide tracker.
/// </summary>
public sealed class RowActivatorTracker {
    private readonly int[]? m_failure;
    private readonly HeldOrderTracker? m_heldTracker;
    private readonly BindingActivatorMode m_mode;
    private readonly IReadOnlyList<string> m_sequence;
    private readonly ulong? m_timeoutTicks;
    private bool m_heldOpen;
    private int m_tapProgress;
    private ulong m_lastAcceptedTick;

    /// <summary>Initializes a new instance of the <see cref="RowActivatorTracker"/> class.</summary>
    /// <param name="activator">The activator definition this tracker resolves.</param>
    public RowActivatorTracker(BindingActivatorDefinition activator) {
        ArgumentNullException.ThrowIfNull(argument: activator);

        m_mode = activator.Mode;
        m_sequence = activator.Sequence;
        m_timeoutTicks = ((activator.TimeoutTicks is { } ticks)
            ? (ulong)ticks
            : null);

        if (m_mode == BindingActivatorMode.Held) {
            m_heldTracker = new HeldOrderTracker(
                modifierCount: m_sequence.Count,
                pressThreshold: 0.5f,
                releaseThreshold: 0.4f
            );
        } else {
            m_failure = ComputeFailureFunction(sequence: m_sequence);
        }
    }

    /// <summary>Applies one signal. Signals whose source names no sequence member are still fed to a
    /// <see cref="BindingActivatorMode.Tapped"/> tracker (an off-sequence press is WRONG INPUT — it resets
    /// progress), but are no-ops for a <see cref="BindingActivatorMode.Held"/> tracker (an unrelated control's
    /// state cannot open or close a hold gate).</summary>
    /// <param name="signal">The signal, in the router's deterministic capture order.</param>
    /// <returns>The transition this signal produced, if any.</returns>
    public RowActivatorTransition Apply(in InputSignal signal) {
        return ((m_mode == BindingActivatorMode.Held)
            ? ApplyHeld(signal: in signal)
            : ApplyTapped(signal: in signal));
    }

    /// <summary>Resets the tracker to its empty state (focus loss, a page/group flip that takes the owning row out
    /// of scope, or a profile reload) — releases every held member and abandons any partial tap progress.</summary>
    public void Reset() {
        m_heldTracker?.Reset();
        m_heldOpen = false;
        m_tapProgress = 0;
        m_lastAcceptedTick = 0UL;
    }

    private RowActivatorTransition ApplyHeld(in InputSignal signal) {
        var index = IndexOf(source: signal.Source);

        if (index < 0) {
            return RowActivatorTransition.None;
        }

        var released = (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled);
        var value = (released
            ? 0f
            : signal.Value.AsAxis1D);

        if (!m_heldTracker!.Set(
            index: index,
            value: value
        )) {
            return RowActivatorTransition.None;
        }

        var held = m_heldTracker.HeldOrder;
        var wasOpen = m_heldOpen;
        var isOpen = IsExactOrder(heldOrder: held);

        m_heldOpen = isOpen;

        if (
            isOpen &&
            !wasOpen
        ) {
            return RowActivatorTransition.Opened;
        }
        if (
            !isOpen &&
            wasOpen
        ) {
            return RowActivatorTransition.Closed;
        }

        return RowActivatorTransition.None;
    }

    // The gate is open exactly when every sequence member is held, in the SEQUENCE'S OWN declared order — held
    // indices are assigned 0..N-1 in that same order, so "held order equals [0, 1, ..., N-1]" is exactly "every
    // member joined, in order, and none has left".
    private bool IsExactOrder(ReadOnlySpan<int> heldOrder) {
        if (heldOrder.Length != m_sequence.Count) {
            return false;
        }

        for (var position = 0; (position < heldOrder.Length); position++) {
            if (heldOrder[position] != position) {
                return false;
            }
        }

        return true;
    }

    // A proper KMP (Knuth-Morris-Pratt) failure-function walk: on a mismatch, fall back through the longest
    // proper prefix of what's matched so far that is ALSO a suffix of it, rather than discarding all progress or
    // only special-casing a restart against step 0. A tapped sequence permits repeats (e.g. [a, a, b]), and a
    // sequence with a repeated PREFIX needs the full walk: under a, a, a, b a naive "restart at 1 iff this press
    // equals step 0" reset would reset to 0 on the third "a" and never see the fourth press complete it, because
    // the third "a" is ALSO a valid one-tap prefix ("a") that reset would discard. KMP never discards a
    // still-valid prefix.
    private RowActivatorTransition ApplyTapped(in InputSignal signal) {
        if (signal.Phase != CommandPhase.Started) {
            // Taps are rising edges only — Active/Completed/Canceled carry no step information either way (a
            // tapped sequence deliberately does not reset on release; see BindingActivatorDefinition's remarks).
            return RowActivatorTransition.None;
        }

        if (
            (m_tapProgress > 0) &&
            (m_timeoutTicks is { } timeout) &&
            ((signal.CaptureTick - m_lastAcceptedTick) > timeout)
        ) {
            m_tapProgress = 0;
        }

        var source = signal.Source;

        while (
            (m_tapProgress > 0) &&
            !string.Equals(
            a: source,
            b: m_sequence[m_tapProgress],
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            m_tapProgress = m_failure![(m_tapProgress - 1)];
        }

        if (string.Equals(
            a: source,
            b: m_sequence[m_tapProgress],
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            m_tapProgress++;
            m_lastAcceptedTick = signal.CaptureTick;
        }

        if (m_tapProgress < m_sequence.Count) {
            return RowActivatorTransition.None;
        }

        // A full reset (not Failure[N-1], which KMP would use to keep tracking an OVERLAPPING re-match) — a fired
        // activator requires a genuinely fresh full sequence to fire again, never a shortcut through its own tail.
        m_tapProgress = 0;

        return RowActivatorTransition.Completed;
    }

    // The KMP prefix/failure function: Failure[i] is the length of the longest proper prefix of sequence[0..i]
    // that is also a suffix of it. Computed once, at construction, over the authored (fixed) sequence — the
    // classic O(N) build.
    private static int[] ComputeFailureFunction(IReadOnlyList<string> sequence) {
        var failure = new int[sequence.Count];
        var k = 0;

        for (var i = 1; (i < sequence.Count); i++) {
            while (
                (k > 0) &&
                !string.Equals(
                a: sequence[i],
                b: sequence[k],
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
            ) {
                k = failure[(k - 1)];
            }

            if (string.Equals(
                a: sequence[i],
                b: sequence[k],
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                k++;
            }

            failure[i] = k;
        }

        return failure;
    }
    private int IndexOf(string source) {
        for (var index = 0; (index < m_sequence.Count); index++) {
            if (string.Equals(
                a: m_sequence[index],
                b: source,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return index;
            }
        }

        return -1;
    }
}
