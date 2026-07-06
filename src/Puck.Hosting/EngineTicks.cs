namespace Puck.Hosting;

/// <summary>
/// The engine's deterministic time base. One second is divided into <see cref="PerSecond"/> ticks
/// (<c>50400 = 2⁵·3²·5²·7</c>). The count is chosen so its divisors include every common animation and
/// display rate — 24/25/30/48/50/60/72/90/120/144/240 Hz, the 2⁵ rates 288 and 480 Hz, and animating on
/// 2s/3s/4s — so a frame or update period at any of those rates is a whole number of ticks and never has to
/// be mapped or repeated (no strobing). Retaining the <c>5²</c> factor keeps the 25-based rates (25/50/100/200)
/// exact, which the larger "highly composite" counts that trade <c>5²</c> for the unused prime 11 do not.
/// </summary>
public static class EngineTicks {
    /// <summary>The number of engine ticks in one second (<c>50400</c>).</summary>
    public const ulong PerSecond = 50400UL;

    /// <summary>Returns the whole number of engine ticks in one period of the given rate.</summary>
    /// <param name="ratePerSecond">A rate in hertz; it must divide <see cref="PerSecond"/> exactly.</param>
    /// <returns>The exact period <c><see cref="PerSecond"/> / <paramref name="ratePerSecond"/></c> in engine ticks.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratePerSecond"/> is zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="ratePerSecond"/> does not divide <see cref="PerSecond"/> exactly.</exception>
    public static ulong PerRate(uint ratePerSecond) {
        ArgumentOutOfRangeException.ThrowIfZero(value: ratePerSecond);

        var period = (PerSecond / ratePerSecond);

        // The whole point of the tick base is exact rates. A rate that does not divide PerSecond would be
        // truncated to a slightly different period, forfeiting the strobe-free alignment without any visible
        // failure — so reject it at the call site instead of shipping a subtly-wrong cadence.
        if ((period * ratePerSecond) != PerSecond) {
            throw new ArgumentException(
                message: $"The rate {ratePerSecond} Hz must divide {PerSecond} engine ticks per second exactly; use a divisor of {PerSecond}.",
                paramName: nameof(ratePerSecond)
            );
        }

        return period;
    }
    /// <summary>Converts a count of engine ticks to seconds.</summary>
    /// <param name="ticks">The number of engine ticks to convert.</param>
    /// <returns>The duration of <paramref name="ticks"/> ticks in seconds.</returns>
    public static double ToSeconds(ulong ticks) =>
        ((double)ticks / PerSecond);
}
