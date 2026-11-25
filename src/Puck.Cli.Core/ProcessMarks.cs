using Puck.Confinement;
using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli;

/// <summary>The marks the shared lease directory keeps about running processes (<see cref="WorldReadiness"/> and
/// <see cref="StartIdleness"/> and <see cref="StartProfiles"/>): one file per process in a folder, named by the process id and its UTC start ticks, so a
/// recycled process id never reads as the process that left the mark.</summary>
internal static class ProcessMarks {
    /// <summary>Names a process's mark file.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The process's UTC start ticks.</param>
    /// <returns>The file name.</returns>
    public static string Name(int processId, long startTicks) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{processId}-{startTicks}");

    /// <summary>Writes a process's mark, replacing an earlier one whole so a reader never sees it half written. A failure
    /// to write leaves the process unmarked, which every reader treats as no mark.</summary>
    /// <param name="directory">The folder holding the marks.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The process's UTC start ticks.</param>
    /// <param name="contents">The mark's text.</param>
    public static void Write(string directory, int processId, long startTicks, string contents) {
        try {
            _ = Directory.CreateDirectory(path: directory);
            Puck.Confinement.AtomicFile.WriteAllText(path: Path.Combine(path1: directory, path2: Name(processId: processId, startTicks: startTicks)), contents: contents);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
    }
    /// <summary>Reads the marks of the processes in <paramref name="running"/>, each parsed from the lines of its file.</summary>
    /// <typeparam name="T">The mark's value.</typeparam>
    /// <param name="directory">The folder holding the marks.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id.</param>
    /// <param name="prune">Whether to delete the marks of processes no longer running.</param>
    /// <param name="parse">Reads a value from the lines of a mark and the process it marks, or returns
    /// <see langword="null"/> for lines it does not recognize.</param>
    /// <returns>The value of each marked process still running, by process id and start ticks.</returns>
    public static IReadOnlyDictionary<(int ProcessId, long StartTicks), T> Read<T>(string directory, IReadOnlyDictionary<int, long> running, bool prune, Func<(int ProcessId, long StartTicks), string[], T?> parse) where T : struct {
        var values = new Dictionary<(int ProcessId, long StartTicks), T>();

        foreach (var (id, file) in Among(directory: directory, prune: prune, running: running)) {
            try {
                if (parse(id, File.ReadAllLines(path: file)) is { } value) { values[id] = value; }
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)) { }
        }

        return values;
    }

    /// <summary>Reads which processes have a mark in <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder holding the marks.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id.</param>
    /// <param name="prune">Whether to delete the marks of processes no longer running; a process that started after the
    /// caller listed <paramref name="running"/> but still runs keeps its mark.</param>
    /// <returns>Each marked process still running, with its mark's file.</returns>
    public static IReadOnlyDictionary<(int ProcessId, long StartTicks), string> Among(string directory, IReadOnlyDictionary<int, long> running, bool prune) {
        var marked = new Dictionary<(int, long), string>();

        if (!Directory.Exists(path: directory)) { return marked; }

        foreach (var file in Directory.EnumerateFiles(path: directory)) {
            var name = Path.GetFileName(path: file).Split(separator: '-');

            if ((name.Length == 2) && int.TryParse(s: name[0], provider: CultureInfo.InvariantCulture, result: out var id) && long.TryParse(s: name[1], provider: CultureInfo.InvariantCulture, result: out var ticks) &&
                ((running.TryGetValue(key: id, value: out var started) && (started == ticks)) || Running(processId: id, startTicks: ticks))) {
                marked[(id, ticks)] = file;

                continue;
            }
            if (!prune) { continue; }
            _ = FileRemoval.TryDelete(path: file);
        }

        return marked;
    }
    private static bool Running(int processId, long startTicks) {
        try {
            using var process = Process.GetProcessById(processId: processId);

            return (!process.HasExited && (process.StartTime.ToUniversalTime().Ticks == startTicks));
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return false;
        }
    }
}
