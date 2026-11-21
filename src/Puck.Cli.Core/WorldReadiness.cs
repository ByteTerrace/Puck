using System.Diagnostics;

namespace Puck.Cli;

/// <summary>
/// Whether a World this CLI started has reached steady state, recorded machine-wide so every process's host admission
/// reads it. The launch that started a World watches its output (<see cref="Observe"/>) and marks it ready on the World's
/// own readiness: a headless World once it serves its console (its first answer on standard output, or its operator
/// attachment), a World that opens a device once its renderer is ready or its bakes settle (<c>[engine: ready at tick</c>,
/// <c>[bakes: settled at tick</c>). From then on the host admission counts the World's actual usage instead of reserving
/// the growth to its expected peak. A mark is a file under the shared lease directory named by the World's process id and
/// start time, so a recycled process id never reads as ready, and a reader removes the marks of Worlds that have exited.
/// The same watch records the World's profile (<see cref="StartProfiles"/>): the one its launch expected, made cold when the
/// World reports it is baking meshes, so the peak it reaches is learned under the profile that produced it.
/// </summary>
public sealed class WorldReadiness {
    private const string AttachmentLine = "[world.control: operator attachment ";
    private const string BakeLine = "[sdf.mesh-bake] start ";
    private const string Folder = "world-ready";

    private readonly string? m_mark;
    private readonly bool m_device;
    private readonly (string Root, int ProcessId, long StartTicks)? m_process;
    private readonly Lock m_gate = new();

    private HostStartProfile? m_profile;
    private int m_marked;

    private WorldReadiness(string? mark, bool device, (string Root, int ProcessId, long StartTicks)? process, HostStartProfile? profile) {
        m_device = device;
        m_mark = mark;
        m_process = process;
        m_profile = profile;
    }

    /// <summary>Gets whether this World has reported ready.</summary>
    public bool Ready => (Volatile.Read(location: ref m_marked) != 0);

    /// <summary>Creates the readiness of a just-started World, and records its profile.</summary>
    /// <param name="process">The World's process.</param>
    /// <param name="opensDevice">Whether the World opens a GPU device.</param>
    /// <param name="checkout">The checkout whose shared lease directory holds the marks.</param>
    /// <param name="profile">The profile the launch expects of the World, or <see langword="null"/> for the kind alone.</param>
    /// <returns>The readiness; one whose checkout names no shared lease directory, or whose process has already exited,
    /// records nothing.</returns>
    public static WorldReadiness For(Process process, bool opensDevice, string checkout, HostStartProfile? profile = null) {
        string? mark = null;
        (string Root, int ProcessId, long StartTicks)? identity = null;

        try {
            var root = CliLease.RootAsync(checkout: checkout).GetAwaiter().GetResult();
            var ticks = process.StartTime.ToUniversalTime().Ticks;

            mark = Path.Combine(path1: Directory(root: root), path2: ProcessMarks.Name(processId: process.Id, startTicks: ticks));
            identity = (root, process.Id, ticks);
            if (profile is { } expected) { StartProfiles.Mark(root: root, processId: process.Id, startTicks: ticks, profile: expected); }
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)) { }

        return new WorldReadiness(device: opensDevice, mark: mark, process: identity, profile: profile);
    }
    /// <summary>Returns whether a line a World wrote is its own report of readiness.</summary>
    /// <param name="line">The line.</param>
    /// <param name="stdout">Whether the line came on standard output.</param>
    /// <param name="opensDevice">Whether the World opens a GPU device.</param>
    /// <returns><see langword="true"/> for a readiness report.</returns>
    public static bool Signals(string line, bool stdout, bool opensDevice) => (opensDevice
        ? (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine: ready at tick ") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes: settled at tick "))
        : (stdout || line.StartsWith(comparisonType: StringComparison.Ordinal, value: AttachmentLine)));
    /// <summary>Reads one line the World wrote, marks the World ready on its first readiness report, and marks its profile cold
    /// on its first report of baking a mesh.</summary>
    /// <param name="line">The line.</param>
    /// <param name="stdout">Whether the line came on standard output.</param>
    public void Observe(string line, bool stdout) {
        if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: BakeLine) && (CliLineFields.Count(line: line, name: "baking") > 0)) { MarkCold(); }
        if (Ready || !Signals(line: line, opensDevice: m_device, stdout: stdout)) { return; }

        lock (m_gate) {
            if (Ready) { return; }
            try {
                if (m_mark is not null) {
                    System.IO.Directory.CreateDirectory(path: Path.GetDirectoryName(path: m_mark)!);
                    File.WriteAllBytes(bytes: [], path: m_mark);
                }
                Volatile.Write(location: ref m_marked, value: 1);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // Keep reserving the expected peak and retry the mark on the next readiness report.
            }
        }
    }
    private void MarkCold() {
        lock (m_gate) {
            if ((m_profile is not { Kind: HostStartKind.WindowedWorld, Store: StoreWarmth.Warm } warm) || (m_process is not { } identity)) { return; }

            m_profile = warm.Cold;
            StartProfiles.Mark(root: identity.Root, processId: identity.ProcessId, startTicks: identity.StartTicks, profile: warm.Cold);
        }
    }
    /// <summary>Reads which Worlds have reported ready, by process id and UTC start ticks, and removes the marks of the
    /// Worlds no longer running.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id.</param>
    /// <returns>The ready Worlds among <paramref name="running"/>.</returns>
    public static IReadOnlySet<(int ProcessId, long StartTicks)> ReadyAmong(string root, IReadOnlyDictionary<int, long> running) =>
        ProcessMarks.Among(directory: Directory(root: root), prune: true, running: running).Keys.ToHashSet();

    private static string Directory(string root) => Path.Combine(path1: root, path2: Folder);
}
