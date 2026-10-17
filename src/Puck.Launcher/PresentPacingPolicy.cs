using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;

namespace Puck.Launcher;

/// <summary>The display fact that determined an effective present-rate target.</summary>
public enum PresentPacingBasis {
    /// <summary>No rate could be resolved; presentation remains unpaced.</summary>
    Unbounded,

    /// <summary>An explicit application target, capped only by the fastest rate the display can show.</summary>
    ExplicitTarget,

    /// <summary>An explicitly advertised variable-refresh interval.</summary>
    VariableRefreshRange,

    /// <summary>The active display cadence, with no claim that VRR is available.</summary>
    SignalTiming,
}
/// <summary>A pure, inspectable present-rate policy result.</summary>
/// <param name="TargetHertz">The effective target, or zero when unbounded.</param>
/// <param name="Basis">The fact that selected the target.</param>
/// <param name="RefreshesPerPresent">The display refreshes each present waits for when the swap chain paces the host, or
/// zero when only the host's timer paces it: an explicit target that no whole number of refreshes of a known signal
/// reaches on a display without variable refresh.</param>
/// <param name="TimerBoundsSwapChain">Whether the timer also paces an iteration after the swap chain's wait. A verified
/// variable-refresh display may show each frame as it arrives, so its present queue never waits for a refresh and only
/// the timer holds the target; while it refreshes at its fixed signal (a composed window) the swap chain's wait ends at
/// or after each of the timer's slightly early deadlines, and that wait paces the iteration.</param>
public readonly record struct PresentPacingDecision(double TargetHertz, PresentPacingBasis Basis, uint RefreshesPerPresent, bool TimerBoundsSwapChain) {
    /// <summary>The fraction by which a timer that bounds a swap chain runs ahead of the target. A display refreshing at
    /// its fixed signal then fills the present queue and paces every iteration by its refresh, rather than the timer
    /// drifting behind a refresh whose true period is a few parts per million shorter than the reported signal's and
    /// showing a frame twice.</summary>
    public const double SwapChainTimerLead = 0.001;

    /// <summary>Converts the target into the timer's period in stopwatch ticks, or zero for an unbounded decision. A timer
    /// that bounds a swap chain runs <see cref="SwapChainTimerLead"/> ahead of the target.</summary>
    /// <param name="frequency">The positive stopwatch frequency.</param>
    /// <returns>The timer's period in stopwatch ticks.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frequency"/> is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">The target is negative or not finite.</exception>
    public long ToPeriodTicks(long frequency) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        if (
            !double.IsFinite(d: TargetHertz) ||
            (TargetHertz < 0.0)
        ) {
            throw new InvalidOperationException(message: "A pacing decision must contain a finite, non-negative target.");
        }

        if (TargetHertz == 0.0) {
            return 0L;
        }

        var period = (frequency / (TimerBoundsSwapChain
            ? (TargetHertz * (1.0 + SwapChainTimerLead))
            : TargetHertz
        ));

        if (period >= long.MaxValue) {
            return long.MaxValue;
        }

        return Math.Max(
            val1: 1L,
            val2: ((long)period)
        );
    }
}
/// <summary>Resolves explicit or display-aware presentation pacing from independent signal and VRR facts.</summary>
public static class PresentPacingPolicy {
    /// <summary>
    /// The automatic-target guard below an advertised VRR ceiling, so a frame stays inside the variable-refresh window
    /// rather than waiting at its edge. It is applied only to a positively identified VRR range and never takes the
    /// target below the active signal; unknown/unsupported VRR never receives invented headroom or a fabricated floor.
    /// </summary>
    public const double VariableRefreshCeilingGuardHertz = 3.0;
    /// <summary>The largest relative difference between an explicit target and the rate a whole number of signal refreshes
    /// presents at for the swap chain to pace that target.</summary>
    public const double SwapChainRateTolerance = 0.01;

    // The whole number of signal refreshes per present that reaches an explicit target, or zero when none does.
    private static uint RefreshesReaching(double signalHertz, double targetHertz) {
        var refreshes = Math.Round(a: (signalHertz / targetHertz));

        if (
            (refreshes < 1.0) ||
            (refreshes > ISwapChainPacing.MaxRefreshesPerPresent) ||
            (Math.Abs(value: ((signalHertz / refreshes) - targetHertz)) > (targetHertz * SwapChainRateTolerance))
        ) {
            return 0U;
        }

        return ((uint)refreshes);
    }

    // The fastest rate a verified variable-refresh display shows frames at while its range is engaged: the advertised
    // maximum, which can exceed the desktop signal, or the signal when the protocol leaves the maximum to the video mode.
    // Null without a known signal or a positively identified range.
    private static double? ResolveVariableRefreshCeiling(DisplayTimingSnapshot timing) => (
        timing.Signal.IsKnown &&
        (timing.VariableRefresh.Support == VariableRefreshSupport.Supported) &&
        (timing.VariableRefresh.Range is { } range)
    )
        ? (range.MaximumHertz ?? timing.Signal.Hertz)
        : null;

    /// <summary>Resolves an effective present-rate target.</summary>
    /// <param name="timing">The current display timing snapshot.</param>
    /// <param name="requestedHertz">A positive explicit target, or zero for display-aware automatic pacing.</param>
    /// <returns>The effective target and the fact that selected it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestedHertz"/> is negative or not finite.</exception>
    public static PresentPacingDecision Resolve(DisplayTimingSnapshot timing, double requestedHertz) {
        if (
            !double.IsFinite(d: requestedHertz) ||
            (requestedHertz < 0.0)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(requestedHertz),
                actualValue: requestedHertz,
                message: "A present-rate request must be finite and non-negative."
            );
        }

        var variableRefreshCeiling = ResolveVariableRefreshCeiling(timing: timing);

        if (requestedHertz > 0.0) {
            // An explicit cadence is bounded by the fastest rate the display shows: a variable-refresh range's ceiling, or
            // else the active signal.
            var targetHertz = Math.Min(
                val1: requestedHertz,
                val2: (variableRefreshCeiling ?? (timing.Signal.IsKnown
                    ? timing.Signal.Hertz
                    : double.PositiveInfinity
                ))
            );
            var refreshes = (timing.Signal.IsKnown
                ? RefreshesReaching(
                    signalHertz: timing.Signal.Hertz,
                    targetHertz: targetHertz
                )
                : 0U
            );

            return new PresentPacingDecision(
                Basis: PresentPacingBasis.ExplicitTarget,
                // A variable-refresh display composed at its fixed signal still waits for a refresh, so the swap chain
                // presents on every one even when no whole number of them reaches the target.
                RefreshesPerPresent: ((variableRefreshCeiling is not null)
                    ? Math.Max(
                        val1: 1U,
                        val2: refreshes
                    )
                    : refreshes
                ),
                // A whole number of refreshes presents at the signal's own rate, so the timer and a fixed display agree.
                TargetHertz: ((refreshes > 0U)
                    ? (timing.Signal.Hertz / refreshes)
                    : targetHertz
                ),
                TimerBoundsSwapChain: (variableRefreshCeiling is not null)
            );
        }

        if (
            (variableRefreshCeiling is { } ceiling) &&
            (timing.VariableRefresh.Range is { } range)
        ) {
            return new PresentPacingDecision(
                Basis: PresentPacingBasis.VariableRefreshRange,
                RefreshesPerPresent: 1U,
                TargetHertz: Math.Max(
                    val1: timing.Signal.Hertz,
                    val2: Math.Max(
                        val1: range.MinimumHertz,
                        val2: (ceiling - VariableRefreshCeilingGuardHertz)
                    )
                ),
                TimerBoundsSwapChain: true
            );
        }

        if (timing.Signal.IsKnown) {
            return new PresentPacingDecision(
                Basis: PresentPacingBasis.SignalTiming,
                RefreshesPerPresent: 1U,
                TargetHertz: timing.Signal.Hertz,
                TimerBoundsSwapChain: false
            );
        }

        return new PresentPacingDecision(
            Basis: PresentPacingBasis.Unbounded,
            // The display's vertical blank still paces a swap chain whose rate the host cannot read.
            RefreshesPerPresent: 1U,
            TargetHertz: 0.0,
            TimerBoundsSwapChain: false
        );
    }
}
