namespace Puck.Audio.Simulation;

/// <summary>One named hit-window: a grade and how many ticks off the nearest beat still earns it.</summary>
/// <param name="Grade">The window's name (e.g. "perfect", "good").</param>
/// <param name="ToleranceTicks">The non-negative tick distance from the nearest beat this window still admits.</param>
public readonly record struct JudgeWindow(string Grade, long ToleranceTicks);
/// <summary>Judges how close a submission tick lands to the nearest beat, against a named set of hit windows.
/// <see cref="Evaluate"/> is a pure function of its three arguments — no wall clock, no mutation of
/// <see cref="MusicClock"/> — so the same <c>(tick, clock, windows)</c> input always grades identically, on any
/// machine, on replay. Latency compensation is never read here: a caller wanting one applies it to the submission
/// tick before calling.</summary>
public static class RhythmJudge {
    /// <summary>Grades a submission tick against the clock's beat spacing and a named window set.</summary>
    /// <param name="tick">The submission's tick, in <paramref name="clock"/>'s own elapsed-tick domain.</param>
    /// <param name="clock">The musical clock the judged beat spacing is read from (read-only).</param>
    /// <param name="windows">The candidate windows, evaluated in order; the first whose tolerance admits the
    /// distance wins, so list tightest-tolerance first for the usual "perfect beats good" grading.</param>
    /// <returns>The first matching window, or <see langword="null"/> for a miss.</returns>
    public static JudgeWindow? Evaluate(ulong tick, MusicClock clock, IReadOnlyList<JudgeWindow> windows) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: windows);

        var ticksPerBeat = ((ulong)clock.TicksPerBeat);
        var remainder = (tick % ticksPerBeat);
        var distanceToNextBeat = (ticksPerBeat - remainder);
        var distance = ((long)Math.Min(val1: remainder, val2: distanceToNextBeat));

        foreach (var window in windows) {
            if (distance <= window.ToleranceTicks) {
                return window;
            }
        }

        return null;
    }
}
