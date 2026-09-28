namespace Puck.Hosting;

/// <summary>
/// The engine tick a frame presents: a whole engine tick and the fraction of the next one the frame sits at. It is
/// the presentation clock every time-driven look reads (a clock's phase, a drifting layer's offset, a medium's
/// pulse), taken from the state mirror's delivered ticks and the frame's interpolation fraction, never from a wall
/// clock, so a frame at a given delivered tick and fraction presents the same values on every run.
/// </summary>
/// <remarks>The whole tick is an unsigned 64-bit count, so the clock never wraps within any running time. Periods are
/// reduced in exact integer arithmetic, and only the reduced remainder is ever converted to a floating-point value.</remarks>
/// <param name="Whole">The whole engine ticks.</param>
/// <param name="Fraction">The fraction of the next engine tick, in <c>[0, 1)</c>.</param>
public readonly record struct PresentedTick(ulong Whole, double Fraction) {
    /// <summary>Returns the tick a frame presents at a fraction of the span between two delivered engine ticks: the
    /// earlier at zero and the later at one. A later tick that is not after the earlier one (a reload or a seek)
    /// presents the later one alone.</summary>
    /// <param name="previous">The earlier delivered engine tick.</param>
    /// <param name="current">The later delivered engine tick.</param>
    /// <param name="fraction">The frame's interpolation fraction; a value outside <c>[0, 1]</c> is clamped.</param>
    /// <returns>The presented tick.</returns>
    public static PresentedTick Between(ulong previous, ulong current, float fraction) {
        if (current <= previous) {
            return new PresentedTick(
                Fraction: 0d,
                Whole: current
            );
        }

        var clamped = Math.Clamp(
            max: 1d,
            min: 0d,
            value: ((double)fraction)
        );
        var offset = ((current - previous) * clamped);
        var whole = Math.Floor(d: offset);

        return new PresentedTick(
            Fraction: (offset - whole),
            Whole: (previous + ((ulong)whole))
        );
    }
    /// <summary>Returns the phase of a periodic clock at this tick: how far through its period, in <c>[0, 1)</c>, a
    /// clock of <paramref name="periodTicks"/> engine ticks that stood at phase zero at engine tick
    /// <c>−<paramref name="startTicks"/></c> is now.</summary>
    /// <param name="periodTicks">The period, in engine ticks; at least one.</param>
    /// <param name="startTicks">The ticks the clock is already into its period at engine tick zero.</param>
    /// <returns>The phase, in <c>[0, 1)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="periodTicks"/> is zero.</exception>
    public double Phase(ulong periodTicks, ulong startTicks = 0UL) {
        ArgumentOutOfRangeException.ThrowIfZero(value: periodTicks);

        var into = ((ulong)((((UInt128)(Whole % periodTicks)) + (startTicks % periodTicks)) % periodTicks));
        var phase = ((into + Fraction) / periodTicks);

        return ((phase >= 1d)
            ? (phase - 1d)
            : phase
        );
    }
    /// <summary>Returns a constant rate integrated from engine tick zero to this tick, reduced into
    /// <c>[−<paramref name="modulus"/> / 2, <paramref name="modulus"/> / 2]</c>: a drift's offset, a spin's angle, a
    /// pulse's cycles. The whole seconds and the ticks past them are integrated apart, so the rounding does not grow
    /// with the running time; a consumer whose field repeats every <paramref name="modulus"/> sees no seam where the
    /// value wraps.</summary>
    /// <param name="ratePerSecond">The rate, in units per second.</param>
    /// <param name="modulus">The period the result is reduced by, in the rate's units; positive.</param>
    /// <returns>The reduced integral.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is not positive.</exception>
    public double Integrate(double ratePerSecond, double modulus) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: modulus);

        var seconds = (Whole / EngineTicks.PerSecond);
        var remainder = ((Whole % EngineTicks.PerSecond) + Fraction);
        var wholePart = Math.IEEERemainder(
            x: (ratePerSecond * seconds),
            y: modulus
        );

        return Math.IEEERemainder(
            x: (wholePart + ((ratePerSecond * remainder) / EngineTicks.PerSecond)),
            y: modulus
        );
    }
}
