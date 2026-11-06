using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli;

/// <summary>
/// Whether a World this CLI started has reached steady state, recorded machine-wide so every process's host admission
/// reads it. The launch that started a World watches its output (<see cref="Observe"/>) and marks it ready on the World's
/// own readiness: a headless World once it serves its console (its first answer on standard output, or its operator
/// attachment), a World that opens a device once its renderer is ready or its bakes settle (<c>[engine: ready at tick</c>,
/// <c>[bakes: settled at tick</c>). From then on the host admission counts the World's actual usage instead of reserving
/// the growth to its expected peak. A mark is a file under the shared lease directory named by the World's process id and
/// start time, so a recycled process id never reads as ready, and a reader removes the marks of Worlds that have exited.
/// </summary>
public sealed class WorldReadiness {
    private const string Folder = "world-ready";
    private const string AttachmentLine = "[world.control: operator attachment ";

    private readonly string? m_mark;
    private readonly bool m_device;

    private int m_marked;

    private WorldReadiness(string? mark, bool device) {
        m_device = device;
        m_mark = mark;
    }

    /// <summary>Gets whether this World has reported ready.</summary>
    public bool Ready => (Volatile.Read(location: ref m_marked) != 0);

    /// <summary>Creates the readiness of a just-started World.</summary>
    /// <param name="process">The World's process.</param>
    /// <param name="opensDevice">Whether the World opens a GPU device.</param>
    /// <param name="checkout">The checkout whose shared lease directory holds the marks.</param>
    /// <returns>The readiness; one whose checkout names no shared lease directory, or whose process has already exited,
    /// records nothing.</returns>
    public static WorldReadiness For(Process process, bool opensDevice, string checkout) {
        string? mark = null;

        try {
            mark = Path.Combine(path1: Directory(root: CliLease.RootAsync(checkout: checkout).GetAwaiter().GetResult()), path2: Name(processId: process.Id, startTicks: process.StartTime.ToUniversalTime().Ticks));
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)) { }

        return new WorldReadiness(device: opensDevice, mark: mark);
    }
    /// <summary>Returns whether a line a World wrote is its own report of readiness.</summary>
    /// <param name="line">The line.</param>
    /// <param name="stdout">Whether the line came on standard output.</param>
    /// <param name="opensDevice">Whether the World opens a GPU device.</param>
    /// <returns><see langword="true"/> for a readiness report.</returns>
    public static bool Signals(string line, bool stdout, bool opensDevice) => (opensDevice
        ? (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine: ready at tick ") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes: settled at tick "))
        : (stdout || line.StartsWith(comparisonType: StringComparison.Ordinal, value: AttachmentLine)));
    /// <summary>Reads one line the World wrote, and marks the World ready on its first readiness report.</summary>
    /// <param name="line">The line.</param>
    /// <param name="stdout">Whether the line came on standard output.</param>
    public void Observe(string line, bool stdout) {
        if (Ready || !Signals(line: line, opensDevice: m_device, stdout: stdout) || (Interlocked.Exchange(location1: ref m_marked, value: 1) != 0) || (m_mark is null)) { return; }

        try {
            System.IO.Directory.CreateDirectory(path: Path.GetDirectoryName(path: m_mark)!);
            File.WriteAllBytes(bytes: [], path: m_mark);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // An unwritable mark leaves the World counted by its expected peak, which only ever over-reserves.
        }
    }
    /// <summary>Reads which Worlds have reported ready, by process id and UTC start ticks, and removes the marks of the
    /// Worlds no longer running.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id.</param>
    /// <returns>The ready Worlds among <paramref name="running"/>.</returns>
    public static IReadOnlySet<(int ProcessId, long StartTicks)> ReadyAmong(string root, IReadOnlyDictionary<int, long> running) {
        var ready = new HashSet<(int, long)>();
        var directory = Directory(root: root);

        if (!System.IO.Directory.Exists(path: directory)) { return ready; }

        foreach (var file in System.IO.Directory.EnumerateFiles(path: directory)) {
            var name = Path.GetFileName(path: file).Split(separator: '-');

            if ((name.Length == 2) && int.TryParse(s: name[0], provider: CultureInfo.InvariantCulture, result: out var id) && long.TryParse(s: name[1], provider: CultureInfo.InvariantCulture, result: out var ticks) &&
                running.TryGetValue(key: id, value: out var started) && (started == ticks)) {
                _ = ready.Add(item: (id, ticks));

                continue;
            }
            // A World that started after the caller listed the processes is not in the list yet but still runs.
            if ((name.Length == 2) && int.TryParse(s: name[0], provider: CultureInfo.InvariantCulture, result: out id) && long.TryParse(s: name[1], provider: CultureInfo.InvariantCulture, result: out ticks) && Running(processId: id, startTicks: ticks)) {
                continue;
            }

            try { File.Delete(path: file); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
        }

        return ready;
    }

    private static bool Running(int processId, long startTicks) {
        try {
            using var process = Process.GetProcessById(processId: processId);

            return (!process.HasExited && (process.StartTime.ToUniversalTime().Ticks == startTicks));
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return false;
        }
    }

    private static string Directory(string root) => Path.Combine(path1: root, path2: Folder);
    private static string Name(int processId, long startTicks) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{processId}-{startTicks}");
}
