using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli;

/// <summary>The marks the shared lease directory keeps about running processes (<see cref="WorldReadiness"/> and
/// <see cref="StartIdleness"/>): one file per process in a folder, named by the process id and its UTC start ticks, so a
/// recycled process id never reads as the process that left the mark.</summary>
internal static class ProcessMarks {
    /// <summary>Names a process's mark file.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The process's UTC start ticks.</param>
    /// <returns>The file name.</returns>
    public static string Name(int processId, long startTicks) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{processId}-{startTicks}");

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
            try { File.Delete(path: file); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
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
