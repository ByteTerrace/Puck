using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;

namespace Puck.Launcher;

/// <summary>The display fact that determined an effective present-rate target.</summary>
public enum PresentPacingBasis {
    /// <summary>No rate could be resolved; presentation remains unpaced.</summary>
    Unbounded,

    /// <summary>An explicit application target, capped only by a known active display cadence.</summary>
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
/// zero when the host paces by its own timer: a variable-refresh display, or an explicit target that no whole number
/// of refreshes of a known signal reaches.</param>
public readonly record struct PresentPacingDecision(double TargetHertz, PresentPacingBasis Basis, uint RefreshesPerPresent) {
    /// <summary>Converts the target into stopwatch ticks, or zero for an unbounded decision.</summary>
    /// <param name="frequency">The positive stopwatch frequency.</param>
    /// <returns>The target period in stopwatch ticks.</returns>
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

        var period = (frequency / TargetHertz);

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
    /// The automatic-target guard below an advertised VRR ceiling. It is applied only to a positively identified VRR
    /// range; unknown/unsupported VRR never receives invented headroom or a fabricated floor.
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

    private static double? ResolveVariableRefreshCeiling(DisplayTimingSnapshot timing) {
        if (!timing.Signal.IsKnown) {
            return null;
        }

        var advertisedCeiling = (
            ((timing.VariableRefresh.Support == VariableRefreshSupport.Supported) &&
            (timing.VariableRefresh.Range is { } range))
            ? range.MaximumHertz
            : null
        );

        return (
            (advertisedCeiling is { } advertised)
            ? Math.Min(
                val1: timing.Signal.Hertz,
                val2: advertised
            )
            : timing.Signal.Hertz
        );
    }

    /// <summary>Resolves an effective present-rate target.</summary>
    /// <param name="timing">The current display timing snapshot.</param>
    /// <param name="requestedHertz">A positive explicit target, or zero for display-aware automatic pacing.</param>
    /// <returns>The effective target and the fact that selected it.</returns>
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

        if (requestedHertz > 0.0) {
            // An explicit fixed cadence is bounded by the active display cadence, not by the monitor's adaptive interval.
            var targetHertz = (timing.Signal.IsKnown
                ? Math.Min(
                    val1: requestedHertz,
                    val2: timing.Signal.Hertz
                )
                : requestedHertz
            );

            return new PresentPacingDecision(
                Basis: PresentPacingBasis.ExplicitTarget,
                RefreshesPerPresent: (timing.Signal.IsKnown
                    ? RefreshesReaching(
                        signalHertz: timing.Signal.Hertz,
                        targetHertz: targetHertz
                    )
                    : 0U
                ),
                TargetHertz: targetHertz
            );
        }

        var effectiveCeiling = ResolveVariableRefreshCeiling(timing: timing);

        if (
            (timing.VariableRefresh.Support == VariableRefreshSupport.Supported) &&
            (timing.VariableRefresh.Range is { } range) &&
            (effectiveCeiling is { } variableRefreshCeiling) &&
            (variableRefreshCeiling >= range.MinimumHertz)
        ) {
            return new PresentPacingDecision(
                TargetHertz: Math.Max(
                    val1: range.MinimumHertz,
                    val2: (variableRefreshCeiling - VariableRefreshCeilingGuardHertz)
                ),
                Basis: PresentPacingBasis.VariableRefreshRange,
                // A variable-refresh display shows a frame when it arrives, so the timer's period is the cadence.
                RefreshesPerPresent: 0U
            );
        }

        if (timing.Signal.IsKnown) {
            return new PresentPacingDecision(
                TargetHertz: timing.Signal.Hertz,
                Basis: PresentPacingBasis.SignalTiming,
                RefreshesPerPresent: 1U
            );
        }

        return new PresentPacingDecision(
            Basis: PresentPacingBasis.Unbounded,
            // The display's vertical blank still paces a swap chain whose rate the host cannot read.
            RefreshesPerPresent: 1U,
            TargetHertz: 0.0
        );
    }
}
