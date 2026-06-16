using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

/// <summary>A reusable Win32 high-resolution waitable timer.</summary>
/// <remarks>Created with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+), the
/// timer wakes within roughly half a millisecond of its due time without raising the
/// process-wide timer resolution (timeBeginPeriod) and without spinning — the classic
/// Thread.Sleep/WaitHandle waits quantize to the ~15.6 ms system tick. <see cref="TryCreate"/>
/// returns null where the flag (or the platform) is unsupported so callers keep their
/// coarse fallback. Waits block the calling thread, preserving Win32 message-queue
/// thread affinity. The same kernel timer is re-armed per wait and supports one waiter
/// at a time; when an owner never disposes (process-lifetime clocks), the SafeWaitHandle
/// finalizer releases the handle.</remarks>
public sealed partial class Win32HighResolutionWaitableTimer : IDisposable {
    private const uint CreateWaitableTimerHighResolutionFlag = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    /// <summary>Creates a high-resolution waitable timer, or null where unsupported.</summary>
    public static Win32HighResolutionWaitableTimer? TryCreate() {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var timerHandle = Kernel32.CreateWaitableTimerEx(
            desiredAccess: TimerAllAccess,
            flags: CreateWaitableTimerHighResolutionFlag,
            timerAttributes: 0,
            timerName: 0
        );

        if (timerHandle.IsInvalid) {
            timerHandle.Dispose();
            return null;
        }

        return new Win32HighResolutionWaitableTimer(timerHandle: timerHandle);
    }

    private readonly SafeWaitHandle m_timerHandle;
    private readonly TimerWaitHandle m_waitHandle;

    private Win32HighResolutionWaitableTimer(SafeWaitHandle timerHandle) {
        m_timerHandle = timerHandle;
        m_waitHandle = new TimerWaitHandle(timerHandle: timerHandle);
    }

    public void Dispose() {
        m_waitHandle.Dispose();
        m_timerHandle.Dispose();
    }

    /// <summary>Blocks the calling thread until the relative due time elapses or the
    /// cancellation handle signals.</summary>
    /// <remarks>Returns true when the timer fired and false when
    /// <paramref name="cancellationWaitHandle"/> signaled first. Degenerate due times
    /// clamp to fire immediately (a non-negative value would schedule an absolute
    /// date instead).</remarks>
    /// <exception cref="InvalidOperationException">The kernel rejected arming the timer.</exception>
    public bool WaitOne(TimeSpan dueTime, WaitHandle? cancellationWaitHandle) {
        // Negative due time = relative, in 100 ns units.
        var dueTimeIn100NanosecondUnits = -Math.Max(
            val1: 1L,
            val2: dueTime.Ticks
        );

        if (!Kernel32.SetWaitableTimer(
            completionRoutine: 0,
            completionRoutineArgument: 0,
            dueTime: in dueTimeIn100NanosecondUnits,
            period: 0,
            resume: false,
            timerHandle: m_timerHandle
        )) {
            throw new InvalidOperationException(message: $"SetWaitableTimer failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        if (cancellationWaitHandle is null) {
            _ = m_waitHandle.WaitOne();
            return true;
        }

        return (WaitHandle.WaitAny(waitHandles: [m_waitHandle, cancellationWaitHandle]) == 0);
    }

    private sealed class TimerWaitHandle : WaitHandle {
        public TimerWaitHandle(SafeWaitHandle timerHandle) {
            SafeWaitHandle = timerHandle;
        }
    }
}
