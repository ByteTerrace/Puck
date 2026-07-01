using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Puck.Abstractions;

namespace Puck.Launcher;

/// <summary>
/// The genlock (latency phase-align) controller: biases the render pacer's deadline toward an external producer's frame
/// arrivals (a live camera publishing through <see cref="ExternalPresentClock"/>) so the frame that samples a fresh
/// arrival starts — and presents — as soon after it as possible, while the engine keeps rendering at full VRR rate. The
/// producer's period is estimated from consecutive arrivals (EMA); the phase error (upcoming deadline vs a small guard
/// offset after the latest arrival, wrapped to the shortest direction) drives a light PI filter whose per-frame output
/// is bounded to a small fraction of the render period — so the cadence stays inside the VRR window and the closed-loop
/// present re-anchor is slewed, never fought. Stale arrivals (producer stopped) freeze the controller and bleed off the
/// integral. Silent with no publisher; <c>PUCK_GENLOCK=0</c> disables it. Render-side only — never the fixed-step sim.
/// </summary>
public sealed class GenlockPhaseAligner {
    private readonly ExternalPresentClock m_clock;
    private readonly bool m_enabled;
    private readonly ILogger m_logger;
    private readonly bool m_logPhase;

    private bool m_announced;
    private double m_errorAccumulator;
    private double m_integral;
    private long m_lastArrival;
    private long m_lastVersion;
    private long m_period;
    private int m_sampleCounter;

    /// <summary>Initializes a new instance of the <see cref="GenlockPhaseAligner"/> class.</summary>
    /// <param name="clock">The external arrival clock producers publish into.</param>
    /// <param name="logger">The pacer's logger (the lock announcement + opt-in phase telemetry).</param>
    /// <param name="logPhase">Whether to periodically log the mean absolute phase error (the <c>PUCK_PRESENT_TIMING=1</c> diagnostics opt-in).</param>
    public GenlockPhaseAligner(ExternalPresentClock clock, ILogger logger, bool logPhase) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        m_clock = clock;
        m_enabled = !string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_GENLOCK"), "0", comparisonType: StringComparison.Ordinal);
        m_logger = logger;
        m_logPhase = logPhase;
    }

    /// <summary>Applies the phase-align bias to the pacer's upcoming deadline; a no-op when disabled, when no producer
    /// has published, or when the latest arrival is stale.</summary>
    /// <param name="deadline">The upcoming render deadline (after the period accumulation), in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="renderPeriod">The pacer's render period in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="frequency">The <see cref="Stopwatch"/> frequency.</param>
    /// <returns>The (possibly nudged) deadline.</returns>
    public long Apply(long deadline, long renderPeriod, long frequency) {
        if (!m_enabled || !m_clock.TryRead(arrivalTimestamp: out var arrival, frameVersion: out var version)) {
            return deadline;
        }

        ObserveArrival(arrival: arrival, frequency: frequency, version: version);

        // Staleness is measured against the deadline being paced (≈ now in production; simulation-friendly): a feed
        // whose latest arrival is older than a few of its own periods has stopped.
        if (
            (0L == m_period) ||
            ((deadline - arrival) >= (m_period * 3L))
        ) {
            // No live producer cadence: bleed the integral so a resumed feed re-locks from neutral.
            m_integral *= 0.9;

            return deadline;
        }

        // The deadline GRID's phase relative to the latest arrival: deadlines tile at renderPeriod, so the achievable
        // alignment is shifting that grid (mod renderPeriod) until the grid line nearest each arrival lands a small
        // guard offset after it (enough that a publish just before a deadline is reliably visible to the frame that
        // fires at it). This keeps the FULL render rate — frames between arrivals stay where the grid puts them — and
        // the sustained slew needed to track the camera↔render beat shrinks with render rate (tiny at 120-240 Hz).
        var desiredPhase = Math.Min(frequency / 500L, renderPeriod / 4L); // 2 ms, capped for very fast cadences
        var phase = ((((deadline - arrival) % renderPeriod) + renderPeriod) % renderPeriod);
        var phaseError = (double)(phase - desiredPhase);

        if (phaseError > (renderPeriod / 2L)) {
            phaseError -= renderPeriod;
        }

        var maxNudge = (renderPeriod / 16.0);

        m_integral = Math.Clamp(value: (m_integral + (phaseError / 64.0)), max: maxNudge, min: -maxNudge);

        var nudge = Math.Clamp(value: ((phaseError / 16.0) + m_integral), max: maxNudge, min: -maxNudge);

        LogPhase(frequency: frequency, phaseError: phaseError);

        return (deadline - (long)nudge);
    }

    // Trains the producer-period estimate (EMA over cadence-plausible deltas) and announces the lock once. The delta is
    // divided by the VERSION advance, so a forwarding cadence that skips frames (two arrivals inside one render frame)
    // still measures the true per-frame period rather than a multiple of it.
    private void ObserveArrival(long arrival, long frequency, long version) {
        if (arrival == m_lastArrival) {
            return;
        }

        if ((0L != m_lastArrival) && (version > m_lastVersion)) {
            var delta = ((arrival - m_lastArrival) / (version - m_lastVersion));

            // Sanity: only cadence-plausible per-frame deltas (2 ms .. 1 s) train the estimate.
            if ((delta > (frequency / 500L)) && (delta < frequency)) {
                m_period = ((0L == m_period)
                    ? delta
                    : (m_period + ((delta - m_period) / 8L)));
            }
        }

        m_lastArrival = arrival;
        m_lastVersion = version;

        if (
            !m_announced &&
            (m_period > 0L) &&
            m_logger.IsEnabled(logLevel: LogLevel.Information)
        ) {
            m_announced = true;

            m_logger.LogInformation(
                "Genlock live: phase-aligning the render deadline to external arrivals (~{Hertz:0.#} Hz).",
                ((double)frequency / m_period)
            );
        }
    }
    // Throttled opt-in telemetry: the mean absolute phase error — the convergence proof (unlocked it wanders the whole
    // producer cycle; locked it settles small and holds).
    private void LogPhase(long frequency, double phaseError) {
        m_errorAccumulator += Math.Abs(value: phaseError);

        if (
            m_logPhase &&
            (0 == (++m_sampleCounter % 240)) &&
            m_logger.IsEnabled(logLevel: LogLevel.Information)
        ) {
            m_logger.LogInformation(
                "Genlock phase: mean |error| {Error:0.00} ms over 240 frames (producer ~{Hertz:0.#} Hz).",
                ((m_errorAccumulator / 240.0) * 1000.0 / frequency),
                ((double)frequency / m_period)
            );

            m_errorAccumulator = 0.0;
        }
    }
}
