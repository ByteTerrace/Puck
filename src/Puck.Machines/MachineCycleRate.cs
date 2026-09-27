namespace Puck.Machines;

/// <summary>
/// A machine's master-clock rate as an exact rational: <see cref="Cycles"/> cycles every <see cref="Seconds"/> emulated
/// seconds. Most clocks are a whole number of hertz and use one second; a clock derived from a colour subcarrier is not
/// (the NTSC NES master clock is 236,250,000 cycles every 11 seconds), and rounding it to hertz would drift the machine
/// against the engine's tick base forever.
/// <para>
/// <see cref="Seconds"/> also sets the scale of the host's tick-to-cycle phase: one cycle is
/// <c><see cref="Puck.Hosting.EngineTicks.PerSecond"/> × <see cref="Seconds"/></c> phase units. A core may change
/// <see cref="Cycles"/> while it runs (a clock-multiplier latch), but it keeps <see cref="Seconds"/> for its whole
/// lifetime, so the carried phase never changes scale mid-stream. The rate is therefore never reduced: doubling
/// 53,203,425 cycles every 2 seconds is 106,406,850 cycles every 2 seconds, not 53,203,425 every 1.
/// </para>
/// </summary>
public readonly record struct MachineCycleRate {
    /// <summary>Initializes a new instance of the <see cref="MachineCycleRate"/> struct.</summary>
    /// <param name="cycles">The master-clock cycles that elapse every <paramref name="seconds"/> emulated seconds.</param>
    /// <param name="seconds">The whole emulated seconds <paramref name="cycles"/> spans; it must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is zero.</exception>
    public MachineCycleRate(ulong cycles, ulong seconds = 1UL) {
        ArgumentOutOfRangeException.ThrowIfZero(value: seconds);

        Cycles = cycles;
        Seconds = seconds;
    }

    /// <summary>Gets the master-clock cycles that elapse every <see cref="Seconds"/> emulated seconds.</summary>
    public ulong Cycles { get; }
    /// <summary>Gets the whole emulated seconds <see cref="Cycles"/> spans, which is also the scale of the host's
    /// carried tick-to-cycle phase.</summary>
    public ulong Seconds { get; }

    /// <summary>Converts a cycle span into emulated seconds for presentation, such as a status readout; never feed the
    /// result back into machine state.</summary>
    /// <param name="cycles">The cycle span to convert.</param>
    /// <returns>The span in emulated seconds, or zero when the rate is zero.</returns>
    public double ToSeconds(long cycles) =>
        ((Cycles == 0UL)
            ? 0.0
            : ((((double)cycles) * Seconds) / Cycles)
        );
}
