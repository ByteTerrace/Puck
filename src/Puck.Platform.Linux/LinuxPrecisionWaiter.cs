using System.Diagnostics;
using Puck.Abstractions.Pacing;
using Puck.Platform.Linux.Interop;

namespace Puck.Platform.Linux;

/// <summary>
/// The Linux <see cref="IPrecisionWaiter"/>: a relative <c>clock_nanosleep</c> on <c>CLOCK_MONOTONIC</c>, which wakes
/// within the kernel's timer slack of its deadline where the runtime's millisecond sleep rounds up to a whole
/// millisecond. It blocks the calling thread, never spins and changes no process-wide setting.
/// </summary>
public sealed class LinuxPrecisionWaiter : IPrecisionWaiter {
    private const long NanosecondsPerSecond = 1_000_000_000L;

    private LinuxPrecisionWaiter() { }

    /// <summary>Creates a precision waiter, or <see langword="null"/> off Linux.</summary>
    /// <returns>The waiter, or <see langword="null"/>.</returns>
    public static LinuxPrecisionWaiter? TryCreate() => (OperatingSystem.IsLinux() ? new LinuxPrecisionWaiter() : null);

    /// <inheritdoc/>
    public bool TryWait(TimeSpan duration) {
        Sleep(duration: duration);

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>The event ends the wait for all but the last millisecond of the duration: the whole milliseconds wait on
    /// the event itself, and the sub-millisecond remainder sleeps on the clock, so a wake in that remainder is kept for
    /// the next wait rather than consumed here.</remarks>
    public bool TryWait(TimeSpan duration, WaitHandle wake, out bool woken) {
        ArgumentNullException.ThrowIfNull(argument: wake);

        var milliseconds = ((int)Math.Min(val1: duration.TotalMilliseconds, val2: (int.MaxValue - 1)));

        if (milliseconds < 1) {
            woken = wake.WaitOne(millisecondsTimeout: 0);

            if (!woken) {
                Sleep(duration: duration);
            }

            return true;
        }

        var start = Stopwatch.GetTimestamp();

        woken = wake.WaitOne(millisecondsTimeout: milliseconds);

        if (!woken) {
            Sleep(duration: (duration - Stopwatch.GetElapsedTime(startingTimestamp: start)));
        }

        return true;
    }

    private static void Sleep(TimeSpan duration) {
        if (duration <= TimeSpan.Zero) {
            return;
        }

        var nanoseconds = (duration.Ticks * 100L);
        var request = new Libc.Timespec { Nanoseconds = (nanoseconds % NanosecondsPerSecond), Seconds = (nanoseconds / NanosecondsPerSecond) };

        while (Libc.clock_nanosleep(clock: Libc.ClockMonotonic, flags: 0, remain: out var remain, request: in request) == Libc.Interrupted) {
            request = remain;
        }
    }
}
