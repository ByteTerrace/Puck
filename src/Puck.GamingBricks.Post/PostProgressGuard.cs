using System.Diagnostics;

namespace Puck.GamingBricks.Post;

/// <summary>The battery's hang guard. Every <see cref="Window"/> it asks whether the battery made progress: a stage
/// finished, or the battery process spent at least <see cref="MinimumProcessorTime"/> of processor time. A stage that
/// is emulating, hashing or comparing spends processor time however slowly a loaded host schedules it, so the guard
/// never abandons a stage that is working; a deadlocked stage spends none, and one window without progress abandons
/// every unfinished stage as <see cref="PostVerdict.Infra"/>. Elapsed time alone never decides a verdict.</summary>
/// <param name="Window">The span each progress sample covers; positive.</param>
/// <param name="MinimumProcessorTime">The least processor time the battery process spends across one window, with no
/// stage finishing, that still counts as progress; positive.</param>
/// <param name="Time">The clock the windows are measured on.</param>
/// <param name="ProcessorTime">Reads the battery process's total processor time so far.</param>
public sealed record PostProgressGuard(TimeSpan Window, TimeSpan MinimumProcessorTime, TimeProvider Time, Func<TimeSpan> ProcessorTime) {
    /// <summary>Gets the guard a battery run uses: one-minute windows on the system clock, in which a battery that
    /// spends less than one processor-second and finishes no stage is hung.</summary>
    public static PostProgressGuard Default { get; } = new(
        MinimumProcessorTime: TimeSpan.FromSeconds(seconds: 1),
        ProcessorTime: static () => {
            using var process = Process.GetCurrentProcess();

            return process.TotalProcessorTime;
        },
        Time: TimeProvider.System,
        Window: TimeSpan.FromMinutes(minutes: 1)
    );
}
