namespace Puck.Maths;

/// <summary>
/// The engine's deterministic time base and every conversion into and out of it: rate periods, authored and runtime
/// durations rounded up to whole ticks, and ticks back to seconds. Every project that counts time in engine ticks
/// reads it here, so the base has one definition.
/// </summary>
public static class FixedTickConversion {
    /// <summary>The number of engine ticks in one second (<c>50400 = 2⁵·3²·5²·7</c>). Its divisors include every
    /// common animation and display rate (24, 25, 30, 48, 50, 60, 72, 90, 120, 144, 240, 288 and 480 Hz, and animating
    /// on 2s, 3s and 4s), so a frame or update period at any of them is a whole number of ticks and never has to be
    /// mapped or repeated. Keeping the <c>5²</c> factor keeps the 25-based rates exact, which the larger highly
    /// composite counts that trade it for the unused prime 11 do not.</summary>
    public const ulong TicksPerSecond = 50400UL;

    /// <summary>Converts an authored duration to the smallest whole engine-tick count no less than its exact decimal
    /// value — <see cref="DurationTicks(decimal, ulong)"/> at <see cref="TicksPerSecond"/>.</summary>
    /// <param name="seconds">The authored duration, in seconds.</param>
    /// <returns><paramref name="seconds"/> in engine ticks, rounded up; <c>0</c> when <paramref name="seconds"/> is
    /// zero or negative.</returns>
    /// <exception cref="OverflowException">The rounded tick count exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static ulong DurationEngineTicks(decimal seconds) => DurationTicks(
        ratePerSecond: TicksPerSecond,
        seconds: seconds
    );
    /// <summary>Converts a runtime fixed-point duration to the smallest whole engine-tick count no less than its exact
    /// value — <see cref="DurationTicks(FixedQ4816, ulong)"/> at <see cref="TicksPerSecond"/>.</summary>
    /// <param name="seconds">The duration to convert, a value that is already fixed-point simulation state.</param>
    /// <returns><paramref name="seconds"/> in engine ticks, rounded up; <c>0</c> when <paramref name="seconds"/> is
    /// zero or negative.</returns>
    public static ulong DurationEngineTicks(FixedQ4816 seconds) => DurationTicks(
        ratePerSecond: TicksPerSecond,
        seconds: seconds
    );
    /// <summary>Converts an authored duration to the smallest whole tick count no less than its exact decimal value at
    /// <paramref name="ratePerSecond"/> ticks per second: the rule every compile path that converts authored seconds
    /// shares, so a document's <c>0.1</c> at 30 Hz is three ticks on every clock and never the four its binary
    /// quantization would round up to.</summary>
    /// <param name="seconds">The authored duration, in seconds, exact as the decimal denotes it.</param>
    /// <param name="ratePerSecond">The ticks in one second; <c>0</c> converts every duration to zero ticks.</param>
    /// <returns><paramref name="seconds"/> in ticks, rounded up; <c>0</c> when <paramref name="seconds"/> is zero or
    /// negative.</returns>
    /// <exception cref="OverflowException">The rounded tick count exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static ulong DurationTicks(decimal seconds, ulong ratePerSecond) {
        if (seconds <= 0m) {
            return 0UL;
        }

        var exact = Rational.FromDecimal(value: seconds);
        var ticks = (new Rational(
            Numerator: (exact.Numerator * ratePerSecond),
            Denominator: exact.Denominator
        )).Ceiling();

        if (ticks > ulong.MaxValue) {
            throw new OverflowException(message: "The rounded tick count exceeds the 64-bit tick carrier.");
        }

        return ((ulong)ticks);
    }
    /// <summary>Converts a runtime fixed-point duration to the smallest whole tick count no less than its exact value
    /// at <paramref name="ratePerSecond"/> ticks per second, so a positive duration always advances by at least one
    /// tick; a non-positive duration converts to zero. The same ceiling as <see cref="DurationTicks(decimal, ulong)"/>,
    /// applied to a value that is already fixed-point simulation state — an authored duration takes the decimal
    /// overload, so its ticks come from its exact decimal rather than from its binary quantization.
    /// <see cref="Int128"/> intermediate arithmetic keeps the product exact for every raw value and rate, so a
    /// duration that divides the rate evenly always round-trips to the same tick count.</summary>
    /// <param name="seconds">The duration to convert.</param>
    /// <param name="ratePerSecond">The ticks in one second; <c>0</c> converts every duration to zero ticks.</param>
    /// <returns><paramref name="seconds"/> in ticks, rounded up; <c>0</c> when <paramref name="seconds"/> is zero or
    /// negative.</returns>
    /// <exception cref="OverflowException">The rounded tick count exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static ulong DurationTicks(FixedQ4816 seconds, ulong ratePerSecond) {
        if (seconds <= FixedQ4816.Zero) {
            return 0UL;
        }

        var scaled = (((Int128)seconds.Value) * ratePerSecond);

        return checked((ulong)scaled.CeilingDivide(divisor: ((Int128)(1L << FixedQ4816.FractionBitCount))));
    }
    /// <summary>Returns the whole number of engine ticks in one period of the given rate.</summary>
    /// <param name="ratePerSecond">A rate in hertz; it must divide <see cref="TicksPerSecond"/> exactly.</param>
    /// <returns>The exact period <c><see cref="TicksPerSecond"/> / <paramref name="ratePerSecond"/></c> in engine
    /// ticks.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratePerSecond"/> is zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="ratePerSecond"/> does not divide <see cref="TicksPerSecond"/>
    /// exactly: a rate that did would be truncated to a slightly different period, forfeiting the strobe-free alignment
    /// the tick base exists for without any visible failure.</exception>
    public static ulong PerRate(uint ratePerSecond) {
        ArgumentOutOfRangeException.ThrowIfZero(value: ratePerSecond);

        var period = (TicksPerSecond / ratePerSecond);

        if ((period * ratePerSecond) != TicksPerSecond) {
            throw new ArgumentException(
                message: $"The rate {ratePerSecond} Hz must divide {TicksPerSecond} engine ticks per second exactly; use a divisor of {TicksPerSecond}.",
                paramName: nameof(ratePerSecond)
            );
        }

        return period;
    }
    /// <summary>Converts a whole tick count at <paramref name="ratePerSecond"/> ticks per second to fixed-point seconds,
    /// rounded once to nearest with ties to even.</summary>
    /// <param name="ticks">The tick count.</param>
    /// <param name="ratePerSecond">The ticks in one second; must be positive.</param>
    /// <returns><c>ticks / ratePerSecond</c> seconds, the nearest Q48.16 value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratePerSecond"/> is zero.</exception>
    /// <exception cref="OverflowException">The rounded seconds exceed <see cref="FixedQ4816.MaxValue"/>.</exception>
    public static FixedQ4816 SecondsOf(ulong ticks, ulong ratePerSecond) {
        ArgumentOutOfRangeException.ThrowIfZero(value: ratePerSecond);

        var numerator = (((UInt128)ticks) << FixedQ4816.FractionBitCount);
        var quotient = (numerator / ratePerSecond);
        var remainder = (numerator - (quotient * ratePerSecond));

        quotient = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: (ratePerSecond - remainder),
            distanceToTruncated: remainder,
            truncated: quotient
        );

        return FixedQ4816.FromRawBits(value: checked((long)quotient));
    }
    /// <summary>Converts a count of engine ticks to seconds, for presentation.</summary>
    /// <param name="ticks">The number of engine ticks to convert.</param>
    /// <returns>The duration of <paramref name="ticks"/> ticks in seconds, as a double; a presentation-out value that
    /// never re-enters simulation state.</returns>
    public static double ToSeconds(ulong ticks) =>
        (((double)ticks) / TicksPerSecond);
    /// <summary>Converts a duration to an EXACT whole engine-tick count — the non-rounding sibling of
    /// <see cref="DurationEngineTicks(FixedQ4816)"/>, for a caller that must REFUSE an inexact duration rather than
    /// silently round it. <paramref name="seconds"/> is <see cref="decimal"/> — base-10, exact for any terminating
    /// decimal literal — rather than a binary float: <see cref="FixedQ4816"/> and <see langword="float"/>/
    /// <see langword="double"/> all carry a binary (power-of-two) fraction, which cannot represent most terminating
    /// decimals exactly either (0.1 has no exact binary spelling any more than it has an exact <see cref="TicksPerSecond"/>-tick
    /// spelling), so routing this exactness check through either would reject values a decimal author reasonably
    /// expects to be exact, or accept values that are not. The arithmetic decomposes the decimal's 96-bit integer
    /// mantissa and base-10 scale into <see cref="UInt128"/> values, so neither decimal multiplication nor a
    /// binary-float intermediate can round or overflow.</summary>
    /// <param name="seconds">The duration, parsed directly from decimal text (a JSON number deserializes to
    /// <see cref="decimal"/> without a binary-float intermediate).</param>
    /// <param name="ticks">The exact whole engine-tick count, when this method returns <see langword="true"/>;
    /// <c>0</c> otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="seconds"/> is non-negative and a whole multiple of
    /// <c>1/<see cref="TicksPerSecond"/></c> second (equivalently, a multiple of 1/800 s — the largest
    /// terminating-decimal-compatible divisor of 50400, since only its factors of 2 and 5 can terminate in base 10);
    /// <see langword="false"/> for a negative duration, one that divides 50400 unevenly, or an exact duration whose
    /// tick count does not fit in <see cref="ulong"/>.</returns>
    public static bool TryDurationEngineTicksExact(decimal seconds, out ulong ticks) {
        if (seconds < 0m) {
            ticks = 0UL;

            return false;
        }

        var bits = decimal.GetBits(d: seconds);
        var scale = ((int)((((uint)bits[3]) >> 16) & 0xFFU));
        var unscaled = (((UInt128)((uint)bits[2])) << 64) | (((UInt128)((uint)bits[1])) << 32) | ((uint)bits[0]);
        var denominator = UInt128.One;

        for (var index = 0; (index < scale); index++) {
            denominator *= 10U;
        }

        // A decimal mantissa is 96 bits and TicksPerSecond is 16 bits, so this product always fits UInt128.
        var numerator = (unscaled * TicksPerSecond);
        var wholeTicks = (numerator / denominator);

        if (
            ((numerator % denominator) != UInt128.Zero) ||
            (wholeTicks > ulong.MaxValue)
        ) {
            ticks = 0UL;

            return false;
        }

        ticks = ((ulong)wholeTicks);

        return true;
    }
}
