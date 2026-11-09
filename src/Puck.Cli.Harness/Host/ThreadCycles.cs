using System.Runtime.InteropServices;

namespace Puck.Cli.Host;

/// <summary>
/// Reads the calling thread's own processor work, for a measurement that must not depend on the machine's load: on
/// Windows the cycles the thread has run (<c>QueryThreadCycleTime</c>), on Linux its CPU time in nanoseconds
/// (<c>CLOCK_THREAD_CPUTIME_ID</c>). A thread waiting or descheduled adds nothing, so a delta over code the thread runs
/// alone counts that code's work whatever else the machine does. Deltas are comparable only within one platform, which
/// <see cref="Unit"/> names.
/// </summary>
public static partial class ThreadCycles {
    /// <summary>Gets the unit a reading counts in: <c>cycles</c> on Windows, <c>thread-ns</c> on Linux, or
    /// <c>none</c> where the platform says nothing.</summary>
    public static string Unit => (OperatingSystem.IsWindows() ? "cycles" : (OperatingSystem.IsLinux() ? "thread-ns" : "none"));

    /// <summary>Reads the calling thread's work so far.</summary>
    /// <returns>The thread's cycles (Windows) or CPU nanoseconds (Linux) since it started; zero where the platform does
    /// not say.</returns>
    public static ulong Now() {
        if (OperatingSystem.IsWindows()) {
            return (QueryThreadCycleTime(thread: GetCurrentThread(), cycles: out var cycles) ? cycles : 0UL);
        }
        if (OperatingSystem.IsLinux() && (clock_gettime(clock: ThreadCpuClock, time: out var time) == 0)) {
            return ((((ulong)time.Seconds) * 1_000_000_000UL) + ((ulong)time.Nanoseconds));
        }

        return 0UL;
    }

    private const int ThreadCpuClock = 3;

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct TimeSpec {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport(libraryName: "kernel32.dll")]
    private static partial nint GetCurrentThread();
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);
    [LibraryImport(libraryName: "libc", SetLastError = true)]
    private static partial int clock_gettime(int clock, out TimeSpec time);
}
