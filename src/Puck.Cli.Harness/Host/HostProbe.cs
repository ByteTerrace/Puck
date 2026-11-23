using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Cli.Host;

/// <summary>
/// Reads the machine for <see cref="HostLoadMonitor"/>: CPU busy time since the previous reading, free physical memory,
/// the commit charge against its limit, free disk on the checkout's drive, and the running processes <see cref="HostProcesses"/> classifies, with
/// each tracked start's private bytes, working set and private working set. It uses only cheap operating-system queries
/// (kernel time counters, the memory status, the process list, each process's command line and a tracked start's memory
/// counters) and never starts a process, so running it is never itself heavy or GPU work. Windows reads through kernel32 and
/// ntdll; Linux through <c>/proc</c>.
/// </summary>
/// <param name="checkoutRoot">The checkout whose drive's free disk is read.</param>
public sealed partial class HostProbe(string checkoutRoot) {
    private readonly HostGpuMeter m_gpu = new();
    private (ulong Idle, ulong Total)? m_previous;
    private string? m_leaseRoot;
    private bool m_leaseRootRead;

    /// <summary>Gets this probe's checkout's shared lease root, or null when it has none.</summary>
    public string? LeaseRoot {
        get {
            if (!m_leaseRootRead) {
                m_leaseRootRead = true;
                try { m_leaseRoot = CliLease.RootAsync(checkout: checkoutRoot).GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
            }
            return m_leaseRoot;
        }
    }
    /// <summary>What other observers recorded about the running processes: the Worlds that reported steady state
    /// (<see cref="WorldReadiness"/>), the starts found idle (<see cref="StartIdleness"/>) and the profile each launch recorded
    /// (<see cref="StartProfiles"/>).</summary>
    private readonly record struct ProcessMarkSet(IReadOnlySet<(int ProcessId, long StartTicks)> Ready, IReadOnlyDictionary<(int ProcessId, long StartTicks), IdleStart> Idle, IReadOnlyDictionary<(int ProcessId, long StartTicks), HostStartProfile> Profiles) {
        public static ProcessMarkSet None => new(new HashSet<(int, long)>(), new Dictionary<(int, long), IdleStart>(), new Dictionary<(int, long), HostStartProfile>());
    }
    // The marks of the checkout's shared lease directory; none when the checkout names no such directory.
    private ProcessMarkSet Marks(Dictionary<int, (int Parent, DateTime Started)> tree) {
        if (LeaseRoot is not { } root) { return ProcessMarkSet.None; }

        try {
            var running = tree.Where(predicate: static entry => (entry.Value.Started != DateTime.MinValue)).ToDictionary(keySelector: static entry => entry.Key, elementSelector: static entry => entry.Value.Started.ToUniversalTime().Ticks);

            return new ProcessMarkSet(WorldReadiness.ReadyAmong(root: root, running: running), StartIdleness.Among(root: root, running: running), StartProfiles.Among(root: root, running: running));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return ProcessMarkSet.None;
        }
    }

    // The holds other processes were admitted to, each with its holder's process tree measured. A hold of this process or of
    // one of its ancestors is left out (HostSample.Holds), and so is one whose holder runs inside another counted hold's tree,
    // which already covers it. Only the processes of a hold's tree pay the handles their CPU time and private working set are
    // read through.
    private List<HostLeaseHold> Holds(Dictionary<int, (int Parent, DateTime Started)> tree, Dictionary<int, (double PrivateGb, double WorkingSetGb)> running, int self, IReadOnlyDictionary<(int ProcessId, long StartTicks), IdleStart> idle) {
        List<HostLeaseHold> holds = [];

        if (LeaseRoot is not { } root) { return holds; }

        IReadOnlyList<CliLease.AdmittedHold> admitted;

        try {
            admitted = CliLease.AdmittedHolds(now: DateTimeOffset.UtcNow, root: root);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return holds;
        }

        foreach (var hold in admitted) {
            if ((hold.ProcessId == self) || !tree.TryGetValue(key: hold.ProcessId, value: out var entry) || (entry.Started.ToUniversalTime().Ticks != hold.StartTicks) || ProcessInspection.IsAncestor(pid: hold.ProcessId, ticks: hold.StartTicks)) { continue; }

            int[] members = [hold.ProcessId, .. HostProcesses.Descendants(processes: tree, root: hold.ProcessId)];
            double privateGb = 0, residentGb = 0, cpuSeconds = 0;
            var timed = false;

            foreach (var member in members) {
                var (memberPrivateGb, memberWorkingSetGb) = running.GetValueOrDefault(key: member);
                var resident = ((Puck.Hosting.HostMemory.PrivateResidentBytes(processId: member) is { } bytes) ? (bytes / 1073741824.0) : HostRunningStart.ResidentOf(privateGb: memberPrivateGb, privateResidentGb: double.NaN, workingSetGb: memberWorkingSetGb));

                privateGb += memberPrivateGb;
                residentGb += (double.IsFinite(d: resident) ? resident : 0);
                try {
                    using var process = Process.GetProcessById(processId: member);

                    if (CpuSeconds(process: process) is var seconds && double.IsFinite(d: seconds)) {
                        cpuSeconds += seconds;
                        timed = true;
                    }
                } catch (ArgumentException) { }
            }
            holds.Add(item: new HostLeaseHold(CpuSeconds: (timed ? cpuSeconds : double.NaN), Device: hold.Device, Holder: hold.Holder, Id: hold.ProcessId, IdleSince: (idle.TryGetValue(key: (hold.ProcessId, hold.StartTicks), value: out var idleStart) ? idleStart.Since : null), Kind: hold.Kind, Lease: hold.Lease, Marked: hold.Profile, Members: members, PrivateGb: privateGb, ResidentGb: residentGb, StartTicks: hold.StartTicks));
        }

        return [.. holds.Where(predicate: hold => !holds.Any(predicate: other => (other.Id != hold.Id) && other.Members.Contains(value: hold.Id)))];
    }

    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    /// <summary>Gets the ticks <see cref="CpuTimes"/> counts in one second: 100-nanosecond units on Windows, jiffies on Linux.</summary>
    public static double CpuTicksPerSecond => (OperatingSystem.IsWindows() ? 10_000_000.0 : 100.0);
    /// <summary>Reads the machine's idle and total CPU time, summed over every logical processor, since boot.</summary>
    /// <returns>The idle and total time in <see cref="CpuTicksPerSecond"/> ticks, or <see langword="null"/> when the platform cannot say.</returns>
    public static (ulong Idle, ulong Total)? CpuTimes() {
        if (OperatingSystem.IsWindows()) {
            // Kernel time includes idle time.
            return (GetSystemTimes(idle: out var idle, kernel: out var kernel, user: out var user)
                ? (idle, (kernel + user))
                : null);
        }
        if (OperatingSystem.IsLinux()) {
            var fields = File.ReadLines(path: "/proc/stat").First().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ').Skip(count: 1).Select(selector: static field => ulong.Parse(provider: CultureInfo.InvariantCulture, s: field)).ToArray();

            // user nice system idle iowait irq softirq steal: idle and iowait are not busy.
            return ((fields[3] + fields[4]), fields.Take(count: 8).Aggregate(func: static (sum, field) => (sum + field), seed: 0UL));
        }

        return null;
    }
    // The commit a process holds now (its private bytes) and the physical memory it holds (its working set); zero and NaN
    // when the platform will not say.
    private static (double PrivateGb, double WorkingSetGb) Footprint(Process process) {
        try {
            return ((process.PrivateMemorySize64 / 1073741824.0), (process.WorkingSet64 / 1073741824.0));
        } catch (Exception exception) when ((exception is Win32Exception or InvalidOperationException or NotSupportedException)) {
            return (0, double.NaN);
        }
    }
    /// <summary>Reads the peaks a running process has reached: its private bytes and whole working set now or at the
    /// operating system's own recorded peak, whichever is higher (Windows records both, so a peak between readings still
    /// counts), and its private working set now, which no system records a peak of.</summary>
    /// <param name="process">The running process, whose cached counters are refreshed first.</param>
    /// <returns>The peaks; one with a counter the platform will not report is not <see cref="HostPeaks.Valid"/>.</returns>
    public static HostPeaks PeaksOf(Process process) {
        process.Refresh();

        var (privateGb, workingSetGb) = Footprint(process: process);
        var resident = ((Puck.Hosting.HostMemory.PrivateResidentBytes(processId: process.Id) is { } bytes) ? (bytes / 1073741824.0) : double.NaN);

        try {
            if (OperatingSystem.IsWindows()) { privateGb = Math.Max(val1: privateGb, val2: (process.PeakPagedMemorySize64 / 1073741824.0)); }
            if (double.IsFinite(d: workingSetGb)) { workingSetGb = Math.Max(val1: workingSetGb, val2: (process.PeakWorkingSet64 / 1073741824.0)); }
        } catch (Exception exception) when ((exception is Win32Exception or InvalidOperationException or NotSupportedException)) { }

        return new HostPeaks(PrivateGb: privateGb, ResidentGb: HostRunningStart.ResidentOf(privateGb: privateGb, privateResidentGb: resident, workingSetGb: workingSetGb), WorkingSetGb: workingSetGb);
    }
    /// <summary>Reads the CPU time a process has used in total.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The seconds, or NaN when the platform will not say (the process exited, or belongs to another user).</returns>
    public static double CpuSeconds(Process process) {
        try {
            return process.TotalProcessorTime.TotalSeconds;
        } catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) {
            return double.NaN;
        }
    }
    /// <summary>Reads when a process started.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The local start time, or <see cref="DateTime.MinValue"/> when the platform will not say.</returns>
    public static DateTime StartOf(Process process) {
        try {
            return process.StartTime;
        } catch (Exception exception) when ((exception is Win32Exception or InvalidOperationException or NotSupportedException)) {
            return DateTime.MinValue;
        }
    }
    private static double FreeRamGb() =>
        ((Puck.Hosting.HostMemory.AvailablePhysicalBytes() is { } available) ? (available / 1073741824.0) : double.NaN);

    /// <summary>Computes CPU busy percentage from two counter readings, or NaN when the interval is unusable.</summary>
    /// <param name="before">The previous idle and total counters.</param>
    /// <param name="after">The current idle and total counters.</param>
    /// <returns>The interval's busy percentage.</returns>
    public static double CpuPercent((ulong Idle, ulong Total)? before, (ulong Idle, ulong Total)? after) =>
        (((before is { } previous) && (after is { } current) && (current.Total > previous.Total) &&
            (current.Idle >= previous.Idle) && ((current.Idle - previous.Idle) <= (current.Total - previous.Total)))
            ? (100.0 * (1.0 - ((current.Idle - previous.Idle) / ((double)(current.Total - previous.Total)))))
            : double.NaN);
    /// <summary>Queries the filesystem's available bytes and converts them to gigabytes.</summary>
    /// <param name="directory">The working directory.</param>
    /// <param name="availableBytes">The operating-system query.</param>
    /// <returns>The available gigabytes.</returns>
    public static double FreeDiskGb(string directory, Func<string, ulong> availableBytes) =>
        (availableBytes(arg: directory) / 1073741824.0);

    private static ulong AvailableDiskBytes(string directory) {
        if (OperatingSystem.IsWindows()) {
            // DriveInfo rejects UNC shares. The native query accepts a directory, including a share or mount point.
            var path = (Path.EndsInDirectorySeparator(path: directory) ? directory : (directory + Path.DirectorySeparatorChar));

            if (!GetDiskFreeSpaceEx(available: out var available, directory: path, free: out _, total: out _)) {
                throw new Win32Exception(error: Marshal.GetLastPInvokeError());
            }

            return available;
        }

        return ((ulong)new DriveInfo(driveName: directory).AvailableFreeSpace);
    }

    /// <summary>Takes one reading. The CPU figure covers the time since the previous reading; the first reading waits
    /// <paramref name="firstInterval"/> to have an interval at all.</summary>
    /// <param name="firstInterval">How long the first reading measures CPU over.</param>
    /// <returns>The reading.</returns>
    public HostSample Sample(TimeSpan firstInterval) {
        if (m_previous is null) {
            m_previous = CpuTimes();
            Thread.Sleep(timeout: firstInterval);
        }

        var times = CpuTimes();
        var cpu = CpuPercent(after: times, before: m_previous);

        m_previous = times;

        string? holder = null;
        string? heavy = null;
        var reuse = 0;
        var self = Environment.ProcessId;
        var running = new List<(int Id, string Name, string CommandLine, double PrivateGb, double WorkingSetGb, HostStartKind? Kind, double CpuSeconds)>();
        var tree = new Dictionary<int, (int Parent, DateTime Started)>();

        foreach (var process in Process.GetProcesses()) {
            using (process) {
                var started = StartOf(process: process);

                tree[process.Id] = (ProcessInspection.ParentId(processId: process.Id), started);

                // The probe never matches itself, whatever its own command line says.
                if (process.Id != self) {
                    var (privateGb, workingSetGb) = Footprint(process: process);
                    var name = process.ProcessName;
                    var commandLine = ProcessInspection.CommandLine(processId: process.Id);
                    var kind = HostProcesses.RunningStart(commandLine: commandLine, name: name);

                    // Only a tracked start pays the handle its CPU time is read through.
                    running.Add(item: (process.Id, name, commandLine, privateGb, workingSetGb, kind, ((kind is null) ? double.NaN : CpuSeconds(process: process))));
                }
            }
        }

        // A heavy test run or a device World this process started is its own work, never another's hold on the machine:
        // a process that starts several device Worlds holds the GPU lease for all of them.
        var own = HostProcesses.Descendants(processes: tree, root: self);

        List<HostRunningStart> starts = [];
        var (ready, idle, profiles) = Marks(tree: tree);

        foreach (var (id, name, commandLine, privateGb, workingSetGb, tracked, cpuSeconds) in running) {
            var label = $"{name} {id.ToString(provider: CultureInfo.InvariantCulture)}";
            var startTicks = tree[id].Started.ToUniversalTime().Ticks;
            DateTimeOffset? idleSince = (idle.TryGetValue(key: (id, startTicks), value: out var idleStart) ? idleStart.Since : null);

            // Only a tracked start pays the handle its private working set is read through.
            if (tracked is { } kind) {
                var privateResidentGb = ((Puck.Hosting.HostMemory.PrivateResidentBytes(processId: id) is { } bytes) ? (bytes / 1073741824.0) : double.NaN);

                starts.Add(item: new HostRunningStart(Id: id, Kind: kind, Marked: (profiles.TryGetValue(key: (id, startTicks), value: out var marked) ? marked : null), PrivateGb: privateGb, PrivateResidentGb: privateResidentGb, Ready: ready.Contains(item: (id, startTicks)), StartTicks: startTicks, WorkingSetGb: workingSetGb, CpuSeconds: cpuSeconds, IdleSince: idleSince));
            }

            // A device process that has gone idle gives its place on the GPU back, as an idle start gives back its growth.
            if ((holder is null) && (idleSince is null) && !own.Contains(item: id) && HostProcesses.IsGpuWork(commandLine: commandLine, name: name)) {
                holder = label;
            }
            // A heavy test run that has gone idle is not running its suite, so a quiet window stops waiting for it.
            if ((heavy is null) && (idleSince is null) && !own.Contains(item: id) && HostProcesses.IsHeavyTest(commandLine: commandLine, name: name)) {
                heavy = label;
            }
            if (HostProcesses.IsReuseNode(commandLine: commandLine, name: name)) {
                reuse++;
            }
        }

        var holds = Holds(idle: idle, running: running.ToDictionary(keySelector: static process => process.Id, elementSelector: static process => (process.PrivateGb, process.WorkingSetGb)), self: self, tree: tree);

        // A hold whose work opens a device holds the GPU from its grant, before its device process appears, until it goes idle.
        if ((holder is null) && (holds.FirstOrDefault(predicate: hold => hold.Device && (hold.IdleSince is null) && !own.Contains(item: hold.Id)) is { Holder: not null } device)) {
            holder = $"{device.Holder} (its {device.Lease} lease, PID {device.Id.ToString(provider: CultureInfo.InvariantCulture)})";
        }

        var commit = Puck.Hosting.HostMemory.CommitBytes();
        var memory = ((holder is null) ? null : m_gpu.Read());

        var sample = new HostSample(
            At: DateTimeOffset.UtcNow,
            CommitChargeGb: (commit is { } charged ? (charged.Charge / 1073741824.0) : double.NaN),
            CommitLimitGb: (commit is { } limited ? (limited.Limit / 1073741824.0) : double.NaN),
            CpuPercent: cpu,
            FreeDiskGb: FreeDiskGb(availableBytes: AvailableDiskBytes, directory: checkoutRoot),
            FreeRamGb: FreeRamGb(),
            GpuHolder: holder,
            ReuseNodes: reuse,
            HeavyTestHolder: heavy,
            Starts: starts,
            Holds: holds,
            DeviceTotalGb: (memory?.TotalGb ?? double.NaN),
            DeviceUsedGb: (memory?.UsedGb ?? double.NaN)
        );
        // Existing host samplers (including the benchmark recorder) feed the same tracker while starts run,
        // so a completed/plateaued start teaches later CLI processes its measured peak.
        _ = HostStartTracker.Session.Judge(sample, HostStartKind.Step, HostLoadThresholds.ThisMachine, TimeProvider.System, reserve: false);
        return sample;
    }
}
