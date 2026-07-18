namespace Puck.Commands;

/// <summary>What one applied signal meant to a <see cref="BindingSession"/>.</summary>
public enum BindingSessionEventKind {
    /// <summary>The signal was not a press the session cares about (a release, analog noise inside the hysteresis band, an unbindable value shape, or the session is already complete). Nothing changed.</summary>
    None = 0,

    /// <summary>The first press of the step, on the suggested source — prompt for the remaining confirmations.</summary>
    SuggestedCaptured,

    /// <summary>The first press of the step, on a DIFFERENT source than suggested — the host should point this out loudly, then prompt for the remaining confirmations.</summary>
    DeviationCaptured,

    /// <summary>A repeat press of the captured source; more confirmations remain.</summary>
    ConfirmationProgress,

    /// <summary>A confirmation press landed on a different source than the captured one — the step reset and must be redone from its first press.</summary>
    Mismatch,

    /// <summary>The step locked in (the required presses all landed on one source) and the session advanced to the next step.</summary>
    StepConfirmed,

    /// <summary>The final step locked in; the session is complete and further signals are ignored.</summary>
    SessionCompleted,

    /// <summary>A press on a reserved source (a page modifier, say) — refused, the step is unchanged.</summary>
    ReservedRejected,

    /// <summary>A press on a source an earlier step already captured — refused, the step is unchanged. <see cref="BindingSessionEvent.ConflictingCommand"/> names the command holding it.</summary>
    ConflictRejected,
}

/// <summary>
/// The outcome of applying one signal to a <see cref="BindingSession"/> — everything a host needs to narrate the
/// session (which prompt, what was pressed, how many presses remain, what went wrong).
/// </summary>
/// <param name="Kind">What the signal meant.</param>
/// <param name="StepIndex">The index of the step the event belongs to.</param>
/// <param name="Source">The pressed source the event is about, or <see langword="null"/> for <see cref="BindingSessionEventKind.None"/>.</param>
/// <param name="PressesRemaining">The presses still needed to lock the current capture in (0 when the step confirmed, reset, or nothing changed).</param>
/// <param name="ConflictingCommand">For <see cref="BindingSessionEventKind.ConflictRejected"/>, the command that already captured <paramref name="Source"/>.</param>
public readonly record struct BindingSessionEvent(
    BindingSessionEventKind Kind,
    int StepIndex,
    string? Source,
    int PressesRemaining,
    string? ConflictingCommand = null
) {
    /// <summary>The nothing-happened event.</summary>
    public static BindingSessionEvent None => new(
        ConflictingCommand: null,
        Kind: BindingSessionEventKind.None,
        PressesRemaining: 0,
        Source: null,
        StepIndex: 0
    );
}
