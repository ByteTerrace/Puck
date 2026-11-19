using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Cli;

/// <summary>Reads another process's parent, command line and private residency through the operating system's cheap queries (ntdll on Windows,
/// <c>/proc</c> on Linux), and judges ancestry from them. It starts no process, so inspecting is never itself heavy work.</summary>
public static partial class ProcessInspection {
    private const uint ProcessBasicInformation = 0;
    private const uint ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport(libraryName: "ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, uint informationClass, void* information, uint length, out uint returned);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(unmanagedType: UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);

    // PROCESS_MEMORY_COUNTERS_EX2 (psapi.h): the documented counters through PrivateUsage, then the private working set and
    // the shared commit. A system that predates the structure refuses its size, which reads as no answer.
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2 {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    /// <summary>Reads how much of another process's working set is its own private memory: the pages no other process
    /// shares and the system cannot drop without writing them out. On Windows it is the private working set
    /// (<c>PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize</c>), which counts every resident page of a private region, a
    /// graphics driver's write-combined host views included, and leaves out mapped files and images, whose clean pages the
    /// system shares between processes and reclaims under pressure (NVIDIA's Direct3D 12 shader disk cache is about a gibibyte
    /// of a windowed World's working set). On Linux it is <c>/proc/&lt;pid&gt;/status</c>'s <c>RssAnon</c> plus
    /// <c>RssShmem</c>: shared memory is swap-backed, not a clean file page.</summary>
    /// <param name="processId">The process to inspect.</param>
    /// <returns>The bytes, or <see langword="null"/> when they cannot be read (the process exited, belongs to another user,
    /// or the system predates the counter).</returns>
    public static long? PrivateResidentBytes(int processId) {
        if (OperatingSystem.IsLinux()) {
            try {
                long? anonymous = null, shared = null;

                foreach (var line in File.ReadLines(path: $"/proc/{processId.ToString(provider: CultureInfo.InvariantCulture)}/status")) {
                    var parts = line.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

                    if ((parts.Length < 2) || !long.TryParse(provider: CultureInfo.InvariantCulture, result: out var kilobytes, s: parts[1], style: NumberStyles.None)) { continue; }
                    switch (parts[0]) {
                        case "RssAnon:": anonymous = (kilobytes * 1024); break;
                        case "RssShmem:": shared = (kilobytes * 1024); break;
                    }
                }

                return ((anonymous is { } resident) ? (resident + (shared ?? 0)) : null);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return null;
            }
        }
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var process = OpenProcess(access: ProcessQueryLimitedInformation, inherit: false, processId: ((uint)processId));

        if (process == 0) {
            return null;
        }

        try {
            var counters = new ProcessMemoryCountersEx2 { Size = ((uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()) };

            return (GetProcessMemoryInfo(counters: ref counters, process: process, size: counters.Size)
                ? ((long)Math.Min(val1: ((ulong)counters.PrivateWorkingSetSize), val2: ((ulong)long.MaxValue)))
                : null);
        } finally {
            _ = CloseHandle(handle: process);
        }
    }

    /// <summary>Reads another process's command line.</summary>
    /// <param name="processId">The process to inspect.</param>
    /// <returns>The command line, or empty when it cannot be read (the process exited, or belongs to another user).</returns>
    public static unsafe string CommandLine(int processId) {
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
    /// <summary>Whether the named process, with the given UTC start ticks, is a live ancestor of this process.</summary>
    /// <param name="pid">The candidate ancestor's process id.</param>
    /// <param name="ticks">The candidate's UTC start ticks, which guard against a recycled id.</param>
    /// <returns>Whether walking this process's parents reaches the candidate.</returns>
    public static bool IsAncestor(int pid, long ticks) {
        var child = Environment.ProcessId;
        var visited = new HashSet<int>();

        try {
            while (visited.Add(item: child)) {
                using var childProcess = System.Diagnostics.Process.GetProcessById(processId: child);
                var parent = ParentId(processId: child);

                if (parent <= 0) { return false; }
                using var parentProcess = System.Diagnostics.Process.GetProcessById(processId: parent);
                var started = parentProcess.StartTime.ToUniversalTime();

                if (started > childProcess.StartTime.ToUniversalTime()) { return false; }
                if (parent == pid) { return (started.Ticks == ticks); }
                child = parent;
            }
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)) { }
        return false;
    }
    /// <summary>Reads a process's parent id, or -1 when it cannot be read.</summary>
    /// <param name="processId">The process to inspect.</param>
    /// <returns>The parent process id, or -1.</returns>
    public static unsafe int ParentId(int processId) {
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
}
