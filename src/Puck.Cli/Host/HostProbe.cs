using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Cli.Host;

/// <summary>
/// Reads the machine for <see cref="HostLoadMonitor"/>: CPU busy time since the previous reading, free physical memory,
/// free disk on the checkout's drive, and the running processes <see cref="HostProcesses"/> classifies. It uses only
/// cheap operating-system queries (kernel time counters, the memory status, the process list and each process's command
/// line) and never starts a process, so running it is never itself heavy or GPU work. Windows reads through kernel32 and
/// ntdll; Linux through <c>/proc</c>.
/// </summary>
internal sealed partial class HostProbe(string checkoutRoot) {
    private const uint ProcessBasicInformation = 0;
    private const uint ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private (ulong Idle, ulong Total)? m_previous;

    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [LibraryImport(libraryName: "ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, uint informationClass, void* information, uint length, out uint returned);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(unmanagedType: UnmanagedType.Bool)] bool inherit, uint processId);
    // The command line of another process, or empty when it cannot be read (it exited, or belongs to another user).
    private static unsafe string CommandLine(int processId) {
        if (OperatingSystem.IsLinux()) {
            try {
                return File.ReadAllText(path: $"/proc/{processId.ToString(provider: CultureInfo.InvariantCulture)}/cmdline");
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return string.Empty;
            }
        }
        if (!OperatingSystem.IsWindows()) {
            return string.Empty;
        }

        var process = OpenProcess(access: ProcessQueryLimitedInformation, inherit: false, processId: ((uint)processId));

        if (process == 0) {
            return string.Empty;
        }

        try {
            _ = NtQueryInformationProcess(information: null, informationClass: ProcessCommandLineInformation, length: 0, process: process, returned: out var needed);

            if (needed == 0) {
                return string.Empty;
            }

            var buffer = new byte[needed];

            fixed (byte* start = buffer) {
                if (NtQueryInformationProcess(information: start, informationClass: ProcessCommandLineInformation, length: needed, process: process, returned: out _) != 0) {
                    return string.Empty;
                }

                // A UNICODE_STRING header (length in bytes, then the buffer pointer) followed by the characters.
                var length = *((ushort*)start);
                var characters = *((char**)(start + IntPtr.Size));

                return new string(length: (length / sizeof(char)), startIndex: 0, value: characters);
            }
        } finally {
            _ = CloseHandle(handle: process);
        }
    }
    // The id of the process that started another one, or -1 when it cannot be read.
    private static unsafe int ParentId(int processId) {
        if (OperatingSystem.IsLinux()) {
            try {
                // pid (comm) state ppid …: the name may hold spaces and parentheses, so read after its last ')'.
                var stat = File.ReadAllText(path: $"/proc/{processId.ToString(provider: CultureInfo.InvariantCulture)}/stat");
                var fields = stat[(stat.LastIndexOf(value: ')') + 2)..].Split(separator: ' ');

                return int.Parse(provider: CultureInfo.InvariantCulture, s: fields[1]);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)) {
                return -1;
            }
        }
        if (!OperatingSystem.IsWindows()) {
            return -1;
        }

        var process = OpenProcess(access: ProcessQueryLimitedInformation, inherit: false, processId: ((uint)processId));

        if (process == 0) {
            return -1;
        }

        try {
            // PROCESS_BASIC_INFORMATION: exit status, PEB, affinity, base priority, own id, then the parent's id, each
            // pointer-sized.
            var information = stackalloc nint[6];

            return ((NtQueryInformationProcess(information: information, informationClass: ProcessBasicInformation, length: ((uint)(6 * IntPtr.Size)), process: process, returned: out _) == 0)
                ? ((int)information[5])
                : -1);
        } finally {
            _ = CloseHandle(handle: process);
        }
    }
    // Busy and total CPU time since boot, in the platform's ticks.
    private static (ulong Idle, ulong Total)? CpuTimes() {
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
        var running = new List<(int Id, string Name, string CommandLine)>();
        var tree = new Dictionary<int, (int Parent, DateTime Started)>();

        foreach (var process in Process.GetProcesses()) {
            using (process) {
                DateTime started;

                try {
                    started = process.StartTime;
                } catch (Exception exception) when ((exception is Win32Exception or InvalidOperationException or NotSupportedException)) {
                    started = DateTime.MinValue;
                }

                tree[process.Id] = (ParentId(processId: process.Id), started);

                // The probe never matches itself, whatever its own command line says.
                if (process.Id != self) {
                    running.Add(item: (process.Id, process.ProcessName, CommandLine(processId: process.Id)));
                }
            }
        }

        // A heavy test run this process started is its own work, never another's hold on the machine.
        var own = HostProcesses.Descendants(processes: tree, root: self);

        foreach (var (id, name, commandLine) in running) {
            var label = $"{name} {id.ToString(provider: CultureInfo.InvariantCulture)}";

            if ((holder is null) && HostProcesses.IsGpuWork(commandLine: commandLine, name: name)) {
                holder = label;
            }
            if ((heavy is null) && !own.Contains(item: id) && HostProcesses.IsHeavyTest(commandLine: commandLine, name: name)) {
                heavy = label;
            }
            if (HostProcesses.IsReuseNode(commandLine: commandLine, name: name)) {
                reuse++;
            }
        }

        return new HostSample(
            At: DateTimeOffset.UtcNow,
            CpuPercent: cpu,
            FreeDiskGb: FreeDiskGb(availableBytes: AvailableDiskBytes, directory: checkoutRoot),
            FreeRamGb: FreeRamGb(),
            GpuHolder: holder,
            ReuseNodes: reuse,
            HeavyTestHolder: heavy
        );
    }
}
