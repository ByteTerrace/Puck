namespace Puck.Commands;

/// <summary>The lifecycle of a <see cref="BindingSession"/>.</summary>
public enum BindingSessionStatus {
    /// <summary>Steps remain; the session is waiting on presses.</summary>
    InProgress = 0,

    /// <summary>Every step confirmed; <see cref="BindingSession.Result"/> holds the full capture list.</summary>
    Completed,

    /// <summary>The host gave up on the session (<see cref="BindingSession.Abandon"/>) before the last step
    /// confirmed. Further signals are ignored, and <see cref="BindingSession.Result"/> holds the partial capture
    /// list — which <see cref="BindingSessionResult.Apply"/> folds in cleanly, leaving the unwalked steps on their
    /// existing bindings.</summary>
    Abandoned,
}
/// <summary>
/// The guided-rebind state machine: walks a player through a <see cref="BindingSessionPlan"/> one step at a
/// time, waiting on each prompt forever with no timers, locking a capture in
/// only after <see cref="BindingSessionPlan.RequiredPresses"/> presses of the same source, resetting the step
/// when a confirmation press wanders, and refusing reserved or already-captured sources. Feed it
/// <see cref="InputSignal"/>s in the router's deterministic capture order and its whole life is a pure function
/// of that sequence — no wall clock, no randomness — so a recorded session replays bit-for-bit. The one thing that
/// ends a session early is the host saying so: <see cref="Abandon"/> stops the machine and keeps the partial
/// captures.
/// </summary>
/// <remarks>
/// The machine is deliberately slot-agnostic: it judges every signal it is handed, so the host decides whose
/// presses count (filter to one player's slot for a personal rebind, or feed everything for a shared kiosk).
/// Presses are rising edges only — a digital <see cref="CommandPhase.Started"/>, or an analog
/// <see cref="CommandValueKind.Axis1D"/> crossing the plan's press threshold with hysteresis (so a trigger
/// resting near the threshold never machine-guns confirmations). Releases, held repeats, and multi-dimensional
/// values (sticks, gyro) are ignored, which is also what drains the leftover release of each confirming press
/// between prompts.
/// </remarks>
public sealed class BindingSession {
    private readonly HashSet<string> m_capturedSources;
    private readonly List<BindingSessionCapture> m_captures;
    private readonly HashSet<string> m_held;
    private readonly BindingSessionPlan m_plan;
    private readonly HashSet<string> m_reservedSources;

    private bool m_abandoned;
    private string? m_pendingSource;
    private int m_pressesRemaining;
    // The last Result handed out, invalidated by the only thing that can change it (a confirmed capture). Hosts poll
    // Result to render progress, and rebuilding the capture list on every read would allocate once per frame.
    private BindingSessionResult? m_result;
    private int m_stepIndex;

    /// <summary>Initializes a new instance of the <see cref="BindingSession"/> class.</summary>
    /// <param name="plan">The plan to walk.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan has no steps, requires fewer than one press, or its thresholds
    /// are non-finite or inverted.</exception>
    public BindingSession(BindingSessionPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Steps.Count == 0) {
            throw new ArgumentException(
                message: "a binding session needs at least one step",
                paramName: nameof(plan)
            );
        }

        if (plan.RequiredPresses < 1) {
            throw new ArgumentException(
                message: "a binding session needs at least one press per step",
                paramName: nameof(plan)
            );
        }

        if (
            !float.IsFinite(f: plan.PressThreshold) ||
            !float.IsFinite(f: plan.ReleaseThreshold) ||
            (plan.ReleaseThreshold > plan.PressThreshold)
        ) {
            throw new ArgumentException(
                message: "the thresholds must be finite and release must not exceed press",
                paramName: nameof(plan)
            );
        }

        m_abandoned = false;
        m_captures = new List<BindingSessionCapture>(capacity: plan.Steps.Count);
        m_capturedSources = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        m_held = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        m_pendingSource = null;
        m_plan = plan;
        m_pressesRemaining = 0;
        m_result = null;
        m_reservedSources = new HashSet<string>(
            collection: (plan.ReservedSources ?? []),
            comparer: StringComparer.OrdinalIgnoreCase
        );
        m_stepIndex = 0;
    }

    /// <summary>Gets the steps confirmed so far, in step order.</summary>
    public IReadOnlyList<BindingSessionCapture> Captures => m_captures;
    /// <summary>Gets the step currently being prompted, or <see langword="null"/> when the session is complete.</summary>
    public BindingSessionStep? CurrentStep => ((m_stepIndex < m_plan.Steps.Count)
        ? m_plan.Steps[m_stepIndex]
        : null
    );
    /// <summary>Gets the index of the step currently being prompted (the step count once complete).</summary>
    public int CurrentStepIndex => m_stepIndex;
    /// <summary>Gets the source the current step captured and is confirming, or <see langword="null"/> while waiting on the first press.</summary>
    public string? PendingSource => m_pendingSource;
    /// <summary>Gets the plan the session is walking.</summary>
    public BindingSessionPlan Plan => m_plan;
    /// <summary>Gets the presses still needed to lock the pending capture in (0 while waiting on a first press).</summary>
    public int PressesRemaining => m_pressesRemaining;
    /// <summary>Gets the result over the captures confirmed so far (complete, or partial for an abandoned session).
    /// Cached: the same instance answers every read until the next capture confirms.</summary>
    public BindingSessionResult Result => (m_result ??= new BindingSessionResult(Captures: [.. m_captures,]));
    /// <summary>Gets the session's lifecycle status.</summary>
    public BindingSessionStatus Status => (m_abandoned
        ? BindingSessionStatus.Abandoned
        : ((m_stepIndex < m_plan.Steps.Count)
        ? BindingSessionStatus.InProgress
        : BindingSessionStatus.Completed)
    );

    private BindingSessionEvent Confirm(string source, BindingSessionStep step, int stepIndex) {
        m_captures.Add(item: new BindingSessionCapture(
            ActivateOn: step.ActivateOn,
            Channel: step.Channel,
            Command: step.Command,
            Icon: step.Icon,
            Label: step.Label,
            MatchedSuggestion: string.Equals(
                a: source,
                b: step.SuggestedSource,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ),
            Source: source
        ));
        _ = m_capturedSources.Add(item: source);
        m_pendingSource = null;
        m_pressesRemaining = 0;
        m_result = null;
        m_stepIndex++;

        return new BindingSessionEvent(
            ConflictingCommand: null,
            Kind: ((m_stepIndex < m_plan.Steps.Count)
            ? BindingSessionEventKind.StepConfirmed
            : BindingSessionEventKind.SessionCompleted),
            PressesRemaining: 0,
            Source: source,
            StepIndex: stepIndex
        );
    }
    // A press is a rising edge only, and every source must RELEASE between presses — a digital Started while the
    // source is still held (a platform's key auto-repeat) is not a second press, so a held button can never
    // confirm itself. Analog 1-D signals press when they cross the plan's press threshold and re-arm only below
    // the release threshold — the same hysteresis shape as BindingModifierDefinition, so a trigger fluttering at
    // the threshold is one press. Everything else (releases, cancels, sticks, gyro, orientation, text) is not a
    // bindable press.
    private bool TryDetectPress(in InputSignal signal) {
        if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            _ = m_held.Remove(item: signal.Source);

            return false;
        }

        switch (signal.Value.Kind) {
            case CommandValueKind.Digital:
                return (
                    (signal.Phase == CommandPhase.Started) &&
                    m_held.Add(item: signal.Source)
                );
            case CommandValueKind.Axis1D: {
                    var value = signal.Value.AsAxis1D;

                    if (m_held.Contains(item: signal.Source)) {
                        if (value <= m_plan.ReleaseThreshold) {
                            _ = m_held.Remove(item: signal.Source);
                        }

                        return false;
                    }

                    return (
                        (value >= m_plan.PressThreshold) &&
                        m_held.Add(item: signal.Source)
                    );
                }
            default:
                return false;
        }
    }

    /// <summary>Gives up on the session where it stands — the host's cancel seam (the player backed out, the menu
    /// closed, the device walked away). Idempotent, and refused once every step has confirmed: a completed session
    /// has nothing left to abandon. The captures confirmed so far survive on <see cref="Result"/>, so a host may
    /// still apply the partial rebind.</summary>
    /// <returns><see langword="true"/> when this call abandoned an in-progress session.</returns>
    public bool Abandon() {
        if (Status != BindingSessionStatus.InProgress) {
            return false;
        }

        m_abandoned = true;
        m_pendingSource = null;
        m_pressesRemaining = 0;
        m_held.Clear();

        return true;
    }
    /// <summary>
    /// Applies one signal, in capture order, and reports what it meant. Only rising edges advance the machine;
    /// everything else returns <see cref="BindingSessionEventKind.None"/> against the step still being prompted.
    /// </summary>
    /// <param name="signal">The signal to judge.</param>
    /// <returns>The event the signal produced.</returns>
    public BindingSessionEvent Advance(in InputSignal signal) {
        // The lifecycle check comes FIRST: a finished session must be inert, and TryDetectPress is a mutation (it
        // owns the per-source held set), so running it after the machine has stopped would leave the session's
        // state depending on signals it is no longer judging.
        if (Status != BindingSessionStatus.InProgress) {
            return BindingSessionEvent.None(stepIndex: m_stepIndex);
        }

        if (!TryDetectPress(signal: signal)) {
            return BindingSessionEvent.None(stepIndex: m_stepIndex);
        }

        var source = signal.Source;
        var stepIndex = m_stepIndex;

        if (m_reservedSources.Contains(item: source)) {
            return new BindingSessionEvent(
                ConflictingCommand: null,
                Kind: BindingSessionEventKind.ReservedRejected,
                PressesRemaining: m_pressesRemaining,
                Source: source,
                StepIndex: stepIndex
            );
        }

        if (
            m_capturedSources.Contains(item: source) &&
            !string.Equals(
            a: source,
            b: m_pendingSource,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            return new BindingSessionEvent(
                ConflictingCommand: m_captures.Last(predicate: capture => string.Equals(
                    a: capture.Source,
                    b: source,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )).Command,
                Kind: BindingSessionEventKind.ConflictRejected,
                PressesRemaining: m_pressesRemaining,
                Source: source,
                StepIndex: stepIndex
            );
        }

        var step = m_plan.Steps[stepIndex];

        if (m_pendingSource is null) {
            // The first press of the step captures the candidate; confirmations follow.
            m_pendingSource = source;
            m_pressesRemaining = (m_plan.RequiredPresses - 1);

            if (m_pressesRemaining == 0) {
                return Confirm(
                    source: source,
                    step: step,
                    stepIndex: stepIndex
                );
            }

            return new BindingSessionEvent(
                ConflictingCommand: null,
                Kind: (string.Equals(
                    a: source,
                    b: step.SuggestedSource,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
                ? BindingSessionEventKind.SuggestedCaptured
                : BindingSessionEventKind.DeviationCaptured),
                PressesRemaining: m_pressesRemaining,
                Source: source,
                StepIndex: stepIndex
            );
        }

        if (!string.Equals(
            a: source,
            b: m_pendingSource,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            // A wandering confirmation invalidates the capture: redo the whole step (the calibration protocol's
            // redo-on-mismatch rule — a hesitant player restates their choice instead of locking in an accident).
            m_pendingSource = null;
            m_pressesRemaining = 0;

            return new BindingSessionEvent(
                ConflictingCommand: null,
                Kind: BindingSessionEventKind.Mismatch,
                PressesRemaining: 0,
                Source: source,
                StepIndex: stepIndex
            );
        }

        m_pressesRemaining--;

        if (m_pressesRemaining > 0) {
            return new BindingSessionEvent(
                ConflictingCommand: null,
                Kind: BindingSessionEventKind.ConfirmationProgress,
                PressesRemaining: m_pressesRemaining,
                Source: source,
                StepIndex: stepIndex
            );
        }

        return Confirm(
            source: source,
            step: step,
            stepIndex: stepIndex
        );
    }
}
