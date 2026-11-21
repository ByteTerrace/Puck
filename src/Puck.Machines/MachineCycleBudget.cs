using Puck.Maths;

namespace Puck.Machines;

/// <summary>
/// The machine-facing faces of <see cref="RationalRateAccumulator"/>: the engine-tick budget a host pacer converts
/// into machine cycles at a <see cref="MachineCycleRate"/>, and the snapshot seam a stage captures its cadence
/// through. They sit beside the accumulator rather than on it because the rate and the transfer are this
/// assembly's types.
/// </summary>
public static class MachineCycleBudget {
    /// <summary>Converts an engine-tick budget into the machine cycles it buys at <paramref name="rate"/>, carrying the
    /// fraction of a cycle into the next conversion so a rate whose <see cref="MachineCycleRate.Cycles"/> changes
    /// between budgets carries no drift. The phase is scaled by <see cref="MachineCycleRate.Seconds"/>, which a stream
    /// must keep constant.</summary>
    /// <param name="accumulator">The host pacer's phase.</param>
    /// <param name="ticks">The engine ticks to convert, <see cref="FixedTickConversion.TicksPerSecond"/> to the second.</param>
    /// <param name="rate">The machine's current cycle rate.</param>
    /// <returns>The whole machine cycles the budget buys.</returns>
    /// <exception cref="OverflowException">The product of <paramref name="ticks"/> and
    /// <see cref="MachineCycleRate.Cycles"/>, or of <see cref="FixedTickConversion.TicksPerSecond"/> and
    /// <see cref="MachineCycleRate.Seconds"/>, exceeds <see cref="long.MaxValue"/>.</exception>
    public static long TakeCycleBudget(this ref RationalRateAccumulator accumulator, ulong ticks, MachineCycleRate rate) =>
        accumulator.Advance(
            period: PhasePeriod(rate: rate),
            weight: checked((long)checked((ticks * rate.Cycles)))
        );
    /// <summary>Returns the fewest engine ticks whose budget, taken from this phase at <paramref name="rate"/>, buys at
    /// least <paramref name="cycles"/> machine cycles: the inverse of <see cref="TakeCycleBudget"/>, which a host uses to
    /// end a part of a segment at an instant the machine names in its own cycles. The phase is read, not moved.</summary>
    /// <param name="accumulator">The host pacer's phase.</param>
    /// <param name="cycles">The machine cycles to reach; at least one.</param>
    /// <param name="rate">The machine's current cycle rate.</param>
    /// <returns>The engine ticks, at least one; <see cref="ulong.MaxValue"/> for a rate of zero cycles, which no
    /// number of ticks reaches.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cycles"/> is below one.</exception>
    public static ulong TicksForCycles(this in RationalRateAccumulator accumulator, long cycles, MachineCycleRate rate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1L,
            value: cycles
        );

        if (rate.Cycles == 0UL) {
            return ulong.MaxValue;
        }

        // The budget of t ticks is floor((phase + t × Cycles) / period), so t reaches `cycles` once
        // phase + t × Cycles is at least cycles × period.
        var owed = ((((Int128)cycles) * PhasePeriod(rate: rate)) - accumulator.Phase);
        var ticks = owed.CeilingDivide(divisor: ((Int128)rate.Cycles));

        return ((ulong)Int128.Clamp(
            max: ulong.MaxValue,
            min: Int128.One,
            value: ticks
        ));
    }
    /// <summary>Gets the phase units in one machine cycle at <paramref name="rate"/>: a carried phase is always below
    /// it, which is how a restored phase is validated.</summary>
    /// <param name="rate">The rate whose phase scale to report.</param>
    /// <returns><see cref="FixedTickConversion.TicksPerSecond"/> × <see cref="MachineCycleRate.Seconds"/>.</returns>
    /// <exception cref="OverflowException">The product exceeds <see cref="long.MaxValue"/>.</exception>
    public static long PhasePeriod(MachineCycleRate rate) =>
        checked((long)checked((FixedTickConversion.TicksPerSecond * rate.Seconds)));
    /// <summary>Moves the phase, as one little-endian 64-bit integer, in the transfer's direction — the snapshot seam
    /// for a stage that captures its cadence.</summary>
    /// <typeparam name="TTransfer">The direction: <see cref="StateSaveTransfer"/> or <see cref="StateLoadTransfer"/>.</typeparam>
    /// <param name="accumulator">The phase to save or restore.</param>
    /// <param name="transfer">The direction's writer or reader.</param>
    /// <exception cref="ArgumentOutOfRangeException">A load reads a negative phase, which no capture produces.</exception>
    public static void TransferState<TTransfer>(this ref RationalRateAccumulator accumulator, TTransfer transfer) where TTransfer : struct, IStateTransfer {
        var phase = accumulator.Phase;

        transfer.Int64(value: ref phase);
        accumulator = new(phase: phase);
    }
}
