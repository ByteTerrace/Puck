namespace Puck.GamingBricks;

/// <summary>
/// The drift-free integer resampler both machine families' audio output stages emit through: a phase that accumulates
/// the output rate weighted by each advanced clock unit and yields one sample every time it crosses a whole emulated
/// second's worth of those units, subtracting rather than resetting. Because the phase carries the remainder, the
/// emitted rate is the exact rational <c>rate / period</c> rather than a truncated units-per-sample constant, so a
/// stream of any length holds its rate against emulated time with no accumulated error.
/// <para>
/// Every operation is integer arithmetic in <see cref="long"/>. The caller owns the two rationals: <c>period</c> is
/// the number of weighted units in one emulated second (the SM83 family weights a T-cycle by its half-dot width, so
/// both sides of its ratio are doubled; the GBA weighs a master-clock cycle as one) and the weight passed to
/// <see cref="Advance"/> is the rate scaled by the units that elapsed. Nothing here is emulated state on the SM83
/// side — the output stage is host-facing plumbing — while the GBA APU carries its phase in its snapshot through
/// <see cref="Phase"/>, so a restore resumes the exact cadence the capture severed.
/// </para>
/// </summary>
public struct RationalRateAccumulator {
    private long m_phase;

    /// <summary>Gets or sets the accumulated phase — the snapshot seam for a stage that captures its cadence, and
    /// zero for a freshly configured one.</summary>
    public long Phase {
        readonly get => m_phase;
        set => m_phase = value;
    }

    /// <summary>Advances the phase by one weighted step and returns how many samples that step brings due.</summary>
    /// <param name="weight">The output rate scaled by the clock units this step advanced.</param>
    /// <param name="period">The number of weighted units in one emulated second.</param>
    /// <returns>The number of samples due, which is zero for most steps and above one when a step spans several
    /// sample intervals.</returns>
    public long Advance(long weight, long period) {
        var due = 0L;

        m_phase += weight;

        while (m_phase >= period) {
            m_phase -= period;

            ++due;
        }

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
