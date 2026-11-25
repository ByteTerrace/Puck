using System.Diagnostics;

namespace Puck.Abstractions;

/// <summary>
/// The process's start on the <see cref="Stopwatch"/> timeline: the origin a boot's stages and the host log's lines are
/// timed from, so a wait before the first frame reads as the gap between two stamped lines rather than as unexplained
/// time. The start is read from the operating system once, on first use, and converted to a timestamp then; the
/// conversion's error is the wall clock's against the stopwatch's over the time since the process started, well under a
/// millisecond for a boot. Presentation and diagnostics only: nothing in simulation state reads it.
/// </summary>
public static class ProcessClock {
    private static readonly Lazy<long> Start = new(valueFactory: static () => {
        using var process = Process.GetCurrentProcess();
        var age = (DateTime.Now - process.StartTime);

        return (Stopwatch.GetTimestamp() - ((long)(age.TotalSeconds * Stopwatch.Frequency)));
    });

    /// <summary>Gets the <see cref="Stopwatch"/> timestamp at which the operating system started this process.</summary>
    public static long StartTimestamp => Start.Value;

    /// <summary>Returns the seconds from the process's start to a <see cref="Stopwatch"/> timestamp.</summary>
    /// <param name="timestamp">The timestamp, as <see cref="Stopwatch.GetTimestamp"/> returned it.</param>
    /// <returns>The seconds; negative for a timestamp before the process started.</returns>
    public static double SecondsAt(long timestamp) => Stopwatch.GetElapsedTime(startingTimestamp: StartTimestamp, endingTimestamp: timestamp).TotalSeconds;
}
