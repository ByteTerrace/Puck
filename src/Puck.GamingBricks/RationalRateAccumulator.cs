using Puck.Hosting;

namespace Puck.GamingBricks;

/// <summary>
/// The drift-free integer rate converter every GamingBrick clock crossing goes through: a phase that accumulates a rate
/// weighted by each advanced unit and yields one output unit every time it crosses a whole period, subtracting rather
/// than resetting. Because the phase carries the remainder, the conversion is the exact rational <c>rate / period</c>
/// rather than a truncated units-per-output constant, so a stream of any length holds its rate with no accumulated
/// error.
/// <para>
/// Every operation is integer arithmetic in <see cref="long"/>, and the caller owns the two rationals. Its uses:
/// </para>
/// <para>
/// <b>Audio output.</b> <c>period</c> is the number of weighted clock units in one emulated second and the weight is
/// the output rate scaled by the units that elapsed. The SM83 family weights a T-cycle by its half-dot width, so both
/// sides of its ratio are doubled; the GBA weighs a master-clock cycle as one. The SM83 output stage is host-facing
/// plumbing, while the GBA APU carries its phase in its snapshot through <see cref="TransferState{TTransfer}"/>, so a
/// restore resumes the exact cadence the capture severed.
/// </para>
/// <para>
/// <b>Host pacing.</b> The queued worker and a linked group convert each engine-tick budget into machine cycles with
/// <c>period</c> set to the engine's ticks per second and the weight set to the ticks times the core's cycle rate. That
/// phase is the host accumulator a rewind restores and a durable checkpoint persists as its cycle remainder.
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
    /// <summary>Converts an engine-tick budget into the machine cycles it buys at <paramref name="cyclesPerSecond"/>,
    /// carrying the fraction of a cycle into the next conversion so a rate that changes between budgets carries no
    /// drift.</summary>
    /// <param name="ticks">The engine ticks to convert, <see cref="EngineTicks.PerSecond"/> to the second.</param>
    /// <param name="cyclesPerSecond">The machine's current cycle rate, in cycles per emulated second.</param>
    /// <returns>The whole machine cycles the budget buys.</returns>
    /// <exception cref="OverflowException">The product of <paramref name="ticks"/> and
    /// <paramref name="cyclesPerSecond"/> exceeds <see cref="long.MaxValue"/>.</exception>
    public long TakeCycleBudget(ulong ticks, ulong cyclesPerSecond) =>
        Advance(
            period: ((long)EngineTicks.PerSecond),
            weight: checked((long)(ticks * cyclesPerSecond))
        );
    /// <summary>Resets the phase, starting a fresh stream.</summary>
    public void Reset() =>
        m_phase = 0L;
    /// <summary>Absorbs the weight of steps <see cref="QuietSteps"/> allowed, emitting nothing. Passing more than
    /// that count silently discards the samples the excess would have made due.</summary>
    /// <param name="weight">The total weight of the skipped steps.</param>
    public void Skip(long weight) =>
        m_phase += weight;
    /// <summary>Moves the phase, as one little-endian 64-bit integer, in the transfer's direction — the snapshot seam
    /// for a stage that captures its cadence.</summary>
    /// <typeparam name="TTransfer">The direction: <see cref="StateSaveTransfer"/> or <see cref="StateLoadTransfer"/>.</typeparam>
    /// <param name="transfer">The direction's writer or reader.</param>
    public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer =>
        transfer.Int64(value: ref m_phase);
}
