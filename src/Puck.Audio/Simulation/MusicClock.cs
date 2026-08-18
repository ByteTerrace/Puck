namespace Puck.Audio.Simulation;

/// <summary>Which musical boundaries a <see cref="MusicClock.Advance"/> call crossed — the vocabulary a
/// <see cref="MusicDirector"/> transition's <see cref="MusicTransitionBoundary"/> field evaluates against.</summary>
[Flags]
public enum MusicClockBoundary : byte {
    /// <summary>No boundary crossed this step.</summary>
    None = 0,
    /// <summary>At least one beat boundary was crossed this step.</summary>
    Beat = 1,
    /// <summary>At least one bar boundary was crossed this step (implies <see cref="Beat"/>).</summary>
    Bar = 2,
}
/// <summary>A tick-denominated musical position: one authored tempo (beats per bar, engine ticks per beat) integrated
/// by plain integer tick counting. <see cref="TicksPerBeat"/> is itself a whole tick count rather than a per-second
/// rate, so advancing it is exact addition with no fractional remainder to retain — unlike
/// <see cref="Puck.Maths.FixedRateAccumulator"/>'s rate-over-ticks integration, there is no unrepresentable tail
/// here to carry across calls.</summary>
public sealed class MusicClock {
    private readonly int m_beatsPerBar;
    private readonly long m_ticksPerBeat;

    private ulong m_elapsedTicks;

    /// <summary>Initializes a clock at its authored tempo, elapsed-ticks zero.</summary>
    /// <param name="ticksPerBeat">The positive engine-tick length of one beat.</param>
    /// <param name="beatsPerBar">The positive beat count of one bar.</param>
    public MusicClock(long ticksPerBeat, int beatsPerBar) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: ticksPerBeat, other: 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: beatsPerBar, other: 0);

        m_beatsPerBar = beatsPerBar;
        m_ticksPerBeat = ticksPerBeat;
    }

    /// <summary>Gets the authored beat count of one bar.</summary>
    public int BeatsPerBar => m_beatsPerBar;
    /// <summary>Gets the current bar index since construction.</summary>
    public ulong CurrentBar => (CurrentBeat / ((ulong)m_beatsPerBar));
    /// <summary>Gets the current beat index since construction.</summary>
    public ulong CurrentBeat => (m_elapsedTicks / ((ulong)m_ticksPerBeat));
    /// <summary>Gets the total engine ticks elapsed since construction.</summary>
    public ulong ElapsedTicks => m_elapsedTicks;
    /// <summary>Gets the authored engine-tick length of one beat.</summary>
    public long TicksPerBeat => m_ticksPerBeat;
    /// <summary>Gets the engine-tick length of one bar.</summary>
    public long TicksPerBar => (m_ticksPerBeat * m_beatsPerBar);

    /// <summary>Sets the elapsed-ticks counter directly — a checkpoint restore's one write door, so a captured clock
    /// resumes on the exact tick it was captured at rather than replaying from zero. Never called from ordinary
    /// simulation, which only ever advances through <see cref="Advance"/>.</summary>
    /// <param name="elapsedTicks">The elapsed engine ticks to resume from.</param>
    public void RestoreElapsedTicks(ulong elapsedTicks) {
        m_elapsedTicks = elapsedTicks;
    }
    /// <summary>Advances the clock by a whole engine-tick step and reports which boundaries it crossed.</summary>
    /// <param name="stepTicks">The engine ticks the step advanced by.</param>
    /// <returns>The boundaries crossed during this step.</returns>
    public MusicClockBoundary Advance(ulong stepTicks) {
        var previousBeat = CurrentBeat;
        var previousBar = CurrentBar;

        m_elapsedTicks += stepTicks;

        var boundary = MusicClockBoundary.None;

        if (CurrentBeat != previousBeat) {
            boundary |= MusicClockBoundary.Beat;
        }

        if (CurrentBar != previousBar) {
            boundary |= MusicClockBoundary.Bar;
        }

        return boundary;
    }
}
