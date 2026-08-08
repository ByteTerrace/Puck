namespace Puck.Maths;

/// <summary>
/// Integrates a Q48.16 rate expressed per second over integer time ticks without discarding the sub-raw-unit remainder.
/// </summary>
/// <remarks>
/// <para>
/// Each call evaluates <c>(rate.Raw × elapsedTicks + remainder) / ticksPerSecond</c>. The quotient is the Q48.16
/// quantity advanced during the interval; the signed remainder is retained for the next call. Consequently, a constant
/// rate of one unit per second advances by exactly one represented unit after <c>ticksPerSecond</c> one-tick calls even
/// when no individual step can represent the exact fraction.
/// </para>
/// <para>
/// The time base is bound once at construction: pass <c>ticksPerSecond</c> to the constructor, then call
/// <see cref="Integrate"/> without it. The retained remainder is a numerator over that denominator, so re-interpreting
/// it under a different denominator would fabricate motion; binding removes that transition from the API surface.
/// A default-initialized value (denominator zero) throws from <see cref="Integrate"/> rather than dividing by zero.
/// </para>
/// <para>
/// The remainder is authoritative simulation state. Persist both <see cref="Remainder"/> and <see cref="TicksPerSecond"/>
/// in snapshots and state hashes and restore them together with <see cref="FromRemainder"/>. Call <see cref="Reset"/>
/// whenever the integrated quantity is assigned, clamped, teleported, or otherwise rewritten outside this accumulator.
/// </para>
/// <para>
/// This is a mutable struct: <see cref="Integrate"/> and <see cref="Reset"/> write the receiver in place, so hold it in
/// a writable storage location — a mutable field, an array slot, or a <see langword="ref"/> local. Never hold it in a
/// <see langword="readonly"/> field, a get-only property, an <see langword="in"/> parameter, or behind a
/// <see cref="System.Collections.Generic.List{T}"/> or <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>
/// indexer: each of those hands the mutating members a compiler-inserted defensive copy, with no warning, and every
/// advance is silently discarded — the holder's <see cref="Remainder"/> reads zero forever while the integration
/// degrades into exactly the pre-rounded per-step delta these remarks tell you never to use.
/// </para>
/// </remarks>
public struct FixedRateAccumulator {
    private long m_remainder;
    private long m_ticksPerSecond;

    /// <summary>Initializes an accumulator bound to a fixed positive time base.</summary>
    /// <param name="ticksPerSecond">The positive number of time-base ticks in one second.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticksPerSecond"/> is not positive.</exception>
    public FixedRateAccumulator(long ticksPerSecond) {
        ValidateTicksPerSecond(ticksPerSecond: ticksPerSecond);

        m_remainder = 0L;
        m_ticksPerSecond = ticksPerSecond;
    }

    private FixedRateAccumulator(long remainder, long ticksPerSecond) {
        m_remainder = remainder;
        m_ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Gets the signed numerator remainder, in raw-Q48.16-units × time-ticks.</summary>
    public readonly long Remainder => m_remainder;

    /// <summary>Gets the bound time-base denominator, in ticks per second. Zero for a default-initialized value.</summary>
    public readonly long TicksPerSecond => m_ticksPerSecond;

    /// <summary>Restores an accumulator from a snapshotted remainder and its bound time base.</summary>
    /// <param name="remainder">The signed remainder previously read from <see cref="Remainder"/>.</param>
    /// <param name="ticksPerSecond">The positive time-base denominator previously read from <see cref="TicksPerSecond"/>.</param>
    /// <returns>An accumulator that continues the captured integration exactly.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ticksPerSecond"/> is not positive, or the magnitude of <paramref name="remainder"/> is not
    /// smaller than it.
    /// </exception>
    public static FixedRateAccumulator FromRemainder(long remainder, long ticksPerSecond) {
        ValidateTicksPerSecond(ticksPerSecond: ticksPerSecond);
        ValidateRemainder(remainder: remainder, ticksPerSecond: ticksPerSecond, paramName: nameof(remainder));

        return new(remainder: remainder, ticksPerSecond: ticksPerSecond);
    }

    /// <summary>Integrates the bound per-second rate over an integer tick interval and retains the unrepresentable tail.</summary>
    /// <param name="ratePerSecond">The Q48.16 rate per second.</param>
    /// <param name="elapsedTicks">The number of time-base ticks elapsed.</param>
    /// <returns>The Q48.16 quantity advanced during the interval.</returns>
    /// <exception cref="InvalidOperationException">The accumulator is default-initialized (no time base was bound).</exception>
    /// <exception cref="OverflowException">The integrated quotient cannot fit in Q48.16 raw storage.</exception>
    public FixedQ4816 Integrate(FixedQ4816 ratePerSecond, ulong elapsedTicks) {
        ThrowIfUnbound(ticksPerSecond: m_ticksPerSecond);

        var (deltaRaw, remainder) = IntegrateRaw(
            elapsedTicks: elapsedTicks,
            rateRaw: ratePerSecond.Value,
            remainder: m_remainder,
            ticksPerSecond: m_ticksPerSecond
        );

        m_remainder = remainder;

        return FixedQ4816.FromRawBits(value: deltaRaw);
    }

    /// <summary>Clears the retained sub-raw-unit remainder. The bound time base is preserved.</summary>
    public void Reset() {
        m_remainder = 0L;
    }

    internal static (long DeltaRaw, long Remainder) IntegrateRaw(
        long rateRaw,
        long remainder,
        ulong elapsedTicks,
        long ticksPerSecond
    ) {
        var numerator = (((Int128)rateRaw * (Int128)elapsedTicks) + remainder);
        var denominator = (Int128)ticksPerSecond;

        var (quotient, nextRemainder) = Int128.DivRem(left: numerator, right: denominator);

        return (
            DeltaRaw: checked((long)quotient),
            Remainder: checked((long)nextRemainder)
        );
    }

    // Callers name the parameter themselves so a multi-axis restore reports the axis it rejected rather than a generic
    // 'remainder'. The band is open on both sides; ticksPerSecond is already known positive when this runs.
    internal static void ValidateRemainder(long remainder, long ticksPerSecond, string paramName) {
        if ((remainder <= -ticksPerSecond) || (remainder >= ticksPerSecond)) {
            throw new ArgumentOutOfRangeException(
                actualValue: remainder,
                message: "The remainder magnitude must be smaller than ticksPerSecond.",
                paramName: paramName
            );
        }
    }
    internal static void ValidateTicksPerSecond(long ticksPerSecond) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: ticksPerSecond);
    }

    // A default-initialized value carries denominator zero. Integrating it would divide by zero; fail loudly instead.
    internal static void ThrowIfUnbound(long ticksPerSecond) {
        if (ticksPerSecond <= 0L) {
            throw new InvalidOperationException(
                message: "The accumulator is default-initialized; construct it with a positive ticksPerSecond before integrating."
            );
        }
    }
}

/// <summary>Three independent <see cref="FixedRateAccumulator"/> axes integrated as one fixed-point vector rate.</summary>
/// <remarks>The time base is bound once at construction and shared by all three axes; see <see cref="FixedRateAccumulator"/>.
/// Like the scalar form, this is a mutable struct whose <see cref="Integrate"/> and reset members write the receiver in
/// place: hold it in a writable field, array slot, or <see langword="ref"/> local — never a <see langword="readonly"/>
/// field, get-only property, <see langword="in"/> parameter, or <see cref="System.Collections.Generic.List{T}"/> /
/// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> indexer result, each of which silently discards
/// every advance through a compiler-inserted defensive copy.</remarks>
public struct FixedVector3RateAccumulator {
    private long m_xRemainder;
    private long m_yRemainder;
    private long m_zRemainder;
    private long m_ticksPerSecond;

    /// <summary>Initializes a vector accumulator bound to a fixed positive time base.</summary>
    /// <param name="ticksPerSecond">The positive number of time-base ticks in one second.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticksPerSecond"/> is not positive.</exception>
    public FixedVector3RateAccumulator(long ticksPerSecond) {
        FixedRateAccumulator.ValidateTicksPerSecond(ticksPerSecond: ticksPerSecond);

        m_xRemainder = 0L;
        m_yRemainder = 0L;
        m_zRemainder = 0L;
        m_ticksPerSecond = ticksPerSecond;
    }

    private FixedVector3RateAccumulator(long xRemainder, long yRemainder, long zRemainder, long ticksPerSecond) {
        m_xRemainder = xRemainder;
        m_yRemainder = yRemainder;
        m_zRemainder = zRemainder;
        m_ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Gets the X-axis numerator remainder.</summary>
    public readonly long XRemainder => m_xRemainder;
    /// <summary>Gets the Y-axis numerator remainder.</summary>
    public readonly long YRemainder => m_yRemainder;
    /// <summary>Gets the Z-axis numerator remainder.</summary>
    public readonly long ZRemainder => m_zRemainder;
    /// <summary>Gets the bound time-base denominator, in ticks per second. Zero for a default-initialized value.</summary>
    public readonly long TicksPerSecond => m_ticksPerSecond;

    /// <summary>Restores three snapshotted axis remainders under their shared bound time base.</summary>
    /// <param name="xRemainder">The signed X-axis remainder previously read from <see cref="XRemainder"/>.</param>
    /// <param name="yRemainder">The signed Y-axis remainder previously read from <see cref="YRemainder"/>.</param>
    /// <param name="zRemainder">The signed Z-axis remainder previously read from <see cref="ZRemainder"/>.</param>
    /// <param name="ticksPerSecond">The positive time-base denominator previously read from <see cref="TicksPerSecond"/>.</param>
    /// <returns>A vector accumulator that continues each axis's captured integration exactly.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ticksPerSecond"/> is not positive, or the magnitude of <paramref name="xRemainder"/>,
    /// <paramref name="yRemainder"/>, or <paramref name="zRemainder"/> is not smaller than it. A rejected axis is named
    /// by its own parameter, and the axes are checked in X, Y, Z order.
    /// </exception>
    public static FixedVector3RateAccumulator FromRemainders(
        long xRemainder,
        long yRemainder,
        long zRemainder,
        long ticksPerSecond
    ) {
        FixedRateAccumulator.ValidateTicksPerSecond(ticksPerSecond: ticksPerSecond);
        FixedRateAccumulator.ValidateRemainder(remainder: xRemainder, ticksPerSecond: ticksPerSecond, paramName: nameof(xRemainder));
        FixedRateAccumulator.ValidateRemainder(remainder: yRemainder, ticksPerSecond: ticksPerSecond, paramName: nameof(yRemainder));
        FixedRateAccumulator.ValidateRemainder(remainder: zRemainder, ticksPerSecond: ticksPerSecond, paramName: nameof(zRemainder));

        return new(
            xRemainder: xRemainder,
            yRemainder: yRemainder,
            zRemainder: zRemainder,
            ticksPerSecond: ticksPerSecond
        );
    }

    /// <summary>Integrates the bound vector rate over an integer tick interval and retains every axis's unrepresentable tail.</summary>
    /// <param name="ratePerSecond">The Q48.16 rate per second, one component per axis.</param>
    /// <param name="elapsedTicks">The number of time-base ticks elapsed.</param>
    /// <returns>The Q48.16 quantity each axis advanced during the interval.</returns>
    /// <exception cref="InvalidOperationException">The accumulator is default-initialized (no time base was bound).</exception>
    /// <exception cref="OverflowException">
    /// Any axis's integrated quotient cannot fit in Q48.16 raw storage. The call is atomic across the three axes: all
    /// three quotients are formed before any remainder is stored, so a throw on one axis leaves NONE of them advanced.
    /// </exception>
    public FixedVector3 Integrate(FixedVector3 ratePerSecond, ulong elapsedTicks) {
        FixedRateAccumulator.ThrowIfUnbound(ticksPerSecond: m_ticksPerSecond);

        var x = FixedRateAccumulator.IntegrateRaw(
            rateRaw: ratePerSecond.X.Value,
            remainder: m_xRemainder,
            elapsedTicks: elapsedTicks,
            ticksPerSecond: m_ticksPerSecond
        );
        var y = FixedRateAccumulator.IntegrateRaw(
            rateRaw: ratePerSecond.Y.Value,
            remainder: m_yRemainder,
            elapsedTicks: elapsedTicks,
            ticksPerSecond: m_ticksPerSecond
        );
        var z = FixedRateAccumulator.IntegrateRaw(
            rateRaw: ratePerSecond.Z.Value,
            remainder: m_zRemainder,
            elapsedTicks: elapsedTicks,
            ticksPerSecond: m_ticksPerSecond
        );

        m_xRemainder = x.Remainder;
        m_yRemainder = y.Remainder;
        m_zRemainder = z.Remainder;

        return new FixedVector3(
            X: FixedQ4816.FromRawBits(value: x.DeltaRaw),
            Y: FixedQ4816.FromRawBits(value: y.DeltaRaw),
            Z: FixedQ4816.FromRawBits(value: z.DeltaRaw)
        );
    }

    /// <summary>Clears all retained axis remainders. The bound time base is preserved.</summary>
    public void Reset() {
        m_xRemainder = 0L;
        m_yRemainder = 0L;
        m_zRemainder = 0L;
    }

    /// <summary>Clears the X-axis remainder.</summary>
    public void ResetX() {
        m_xRemainder = 0L;
    }

    /// <summary>Clears the Y-axis remainder.</summary>
    public void ResetY() {
        m_yRemainder = 0L;
    }

    /// <summary>Clears the Z-axis remainder.</summary>
    public void ResetZ() {
        m_zRemainder = 0L;
    }
}
