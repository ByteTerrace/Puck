using System.Globalization;

namespace Puck.Cli;

/// <summary>A start the host admission tracks that has used no CPU for a window, so admission no longer waits on it.</summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="StartTicks">The process's UTC start ticks, which with <paramref name="ProcessId"/> identify it.</param>
/// <param name="Kind">What the start is.</param>
/// <param name="Since">When the process last used CPU, as far as the observer saw.</param>
public readonly record struct IdleStart(int ProcessId, long StartTicks, HostStartKind Kind, DateTimeOffset Since);

/// <summary>
/// Which tracked starts have gone idle, recorded machine-wide so every process's host admission and <c>puck lease list</c>
/// read it. A start that has used no CPU for a window is not growing and is not running the work it holds the machine for,
/// so admission counts only what it holds now and no longer waits for it to exit (<c>HostStartTracker</c>). The observer
/// that sees the window pass writes the mark, and any observer that sees the start use CPU again removes it; a mark is a
/// file under the shared lease directory (<see cref="ProcessMarks"/>) holding the idle-since time and the start's kind.
/// </summary>
public static class StartIdleness {
    private const string Folder = "start-idle";

    private static string Directory(string root) => Path.Combine(path1: root, path2: Folder);

    /// <summary>Records that a start is idle. A failure to write leaves the start unmarked, which only makes admission wait
    /// for it as before.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="start">The idle start.</param>
    public static void Mark(string root, IdleStart start) =>
        ProcessMarks.Write(directory: Directory(root: root), processId: start.ProcessId, startTicks: start.StartTicks, contents: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{start.Since.UtcTicks}\n{start.Kind}\n"));
    /// <summary>Removes a start's idle mark, when it has one.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The process's UTC start ticks.</param>
    public static void Clear(string root, int processId, long startTicks) {
        try { File.Delete(path: Path.Combine(path1: Directory(root: root), path2: ProcessMarks.Name(processId: processId, startTicks: startTicks))); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
    }
    /// <summary>Reads the idle starts among the running processes, and, with <paramref name="prune"/>, removes the marks of
    /// processes no longer running.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id; one not listed is still found when it
    /// runs.</param>
    /// <param name="prune">Whether to remove the marks of processes that have exited.</param>
    /// <returns>The idle starts that still run, by process id and start ticks.</returns>
    public static IReadOnlyDictionary<(int ProcessId, long StartTicks), IdleStart> Among(string root, IReadOnlyDictionary<int, long> running, bool prune = true) =>
        ProcessMarks.Read<IdleStart>(directory: Directory(root: root), prune: prune, running: running, parse: static (id, lines) => ((lines is [var since, var kind, ..] && long.TryParse(s: since, provider: CultureInfo.InvariantCulture, result: out var ticks) && Enum.TryParse<HostStartKind>(value: kind, result: out var parsed))
            ? new IdleStart(Kind: parsed, ProcessId: id.ProcessId, Since: new DateTimeOffset(ticks: ticks, offset: TimeSpan.Zero), StartTicks: id.StartTicks)
            : null));
}
