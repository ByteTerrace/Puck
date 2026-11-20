using System.Runtime.InteropServices;

namespace Puck.Platform.Linux.Interop;

/// <summary>Bindings for the few libc entry points the Linux backends need. libxcb returns
/// events and replies allocated with malloc, which must be released with libc free.</summary>
internal static partial class Libc {
    /// <summary>The <c>CLOCK_MONOTONIC</c> clock id.</summary>
    public const int ClockMonotonic = 1;
    /// <summary>The <c>EINTR</c> error number: a signal ended the sleep early.</summary>
    public const int Interrupted = 4;

    /// <summary>The C <c>struct timespec</c> of a 64-bit Linux target.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Timespec {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc")]
    public static partial void free(nint pointer);
    /// <summary>Sleeps on <paramref name="clock"/> for the relative <paramref name="request"/>; returns zero, or the error
    /// number (not -1 with <c>errno</c>) and, for <see cref="Interrupted"/>, the time left in <paramref name="remain"/>.</summary>
    [LibraryImport("libc")]
    public static partial int clock_nanosleep(int clock, int flags, in Timespec request, out Timespec remain);
}
