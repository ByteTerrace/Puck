namespace Puck.Maths;

/// <summary>
/// The drift-free integer rate converter a clock crossing goes through: a phase that accumulates a rate weighted by
/// each advanced unit and yields one output unit every time it crosses a whole period, subtracting rather than
/// resetting. Because the phase carries the remainder, the conversion is the exact rational <c>rate / period</c>
/// rather than a truncated units-per-output constant, so a stream of any length holds its rate with no accumulated
/// error. The integer sibling of <see cref="FixedRateAccumulator"/>, for a rate that is a ratio of two whole counts
/// rather than a fixed-point quantity per second.
/// <para>
/// Every operation is integer arithmetic in <see cref="long"/>, and the caller owns the two rationals: the weight
/// of one step and the period one output unit is due at. An emulated audio output uses the number of weighted clock
/// units in one emulated second as its period and the output rate scaled by the elapsed units as its weight; a host
/// pacer converts each engine-tick budget into machine cycles with the period set to the engine's ticks per second
/// times the rate's seconds and the weight to the ticks times its cycles. The phase is authoritative state: a
/// snapshot carries it beside the scale it was carried at, and a restore resumes the exact cadence the capture
/// severed.
/// </para>
/// </summary>
public struct RationalRateAccumulator {
    private long m_phase;

    /// <summary>Initializes a new instance of the <see cref="RationalRateAccumulator"/> struct at a captured phase, so
    /// a restored stream resumes the cadence the capture severed.</summary>
    /// <param name="phase">The phase <see cref="Phase"/> reported at capture; it must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> is negative.</exception>
    public RationalRateAccumulator(long phase) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: phase);

        m_phase = phase;
    }

    /// <summary>Gets the accumulated phase, which is zero for a freshly configured stage.</summary>
    public readonly long Phase => m_phase;

    /// <summary>Advances the phase by one weighted step and returns how many output units that step brings due.</summary>
    /// <param name="weight">The rate scaled by the units this step advanced.</param>
    /// <param name="period">The number of weighted units that bring one output unit due; it must be positive.</param>
    /// <returns>The number of output units due, which is zero for most per-cycle steps and above one when a step spans
    /// several intervals.</returns>
    /// <exception cref="OverflowException">The weighted phase exceeds <see cref="long.MaxValue"/>.</exception>
    public long Advance(long weight, long period) {
        m_phase = checked((m_phase + weight));

        if (m_phase < period) {
            return 0L;
        }

        var due = (m_phase / period);

        m_phase -= (due * period);

        return due;
    }
    /// <summary>Returns how many further steps of <paramref name="weight"/> the phase can absorb before the next
    /// sample falls due — the batch a stage fast-forwards through <see cref="Skip"/> while its sink is silent.</summary>
    /// <param name="weight">The per-step weight the steps being counted would each add.</param>
    /// <param name="period">The number of weighted units in one emulated second.</param>
    /// <returns>The number of steps that leave the phase below <paramref name="period"/>.</returns>
    public readonly long QuietSteps(long weight, long period) =>
        (((period - 1L) - m_phase) / weight);
    /// <summary>Resets the phase, starting a fresh stream.</summary>
    public void Reset() =>
        m_phase = 0L;
    /// <summary>Absorbs the weight of steps <see cref="QuietSteps"/> allowed, emitting nothing. Passing more than
    /// that count silently discards the samples the excess would have made due.</summary>
    /// <param name="weight">The total weight of the skipped steps.</param>
    public void Skip(long weight) =>
        m_phase += weight;
}
