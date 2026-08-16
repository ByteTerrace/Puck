namespace Puck.Platform.Windows;

/// <summary>
/// A standalone <see cref="IPrecisionWaiter"/> backed by <see cref="Win32HighResolutionWaitableTimer"/> — the same
/// high-resolution wait a native window exposes for the windowed pacer (<c>Win32NativeWindow</c> implements
/// <see cref="IPrecisionWaiter"/> directly), made available with no window at all for the headless tick host.
/// </summary>
public sealed class Win32PrecisionWaiter : IPrecisionWaiter, IDisposable {
    private readonly Win32HighResolutionWaitableTimer m_timer;

    private Win32PrecisionWaiter(Win32HighResolutionWaitableTimer timer) {
        m_timer = timer;
    }

    /// <summary>Creates a standalone precision waiter, or <see langword="null"/> where the platform/OS version does
    /// not support a high-resolution waitable timer (the caller falls back to a coarse sleep).</summary>
    public static Win32PrecisionWaiter? TryCreate() {
        return ((Win32HighResolutionWaitableTimer.TryCreate() is { } timer)
            ? new Win32PrecisionWaiter(timer: timer)
            : null);
    }
    /// <inheritdoc/>
    public bool TryWait(TimeSpan duration) {
        if (duration <= TimeSpan.Zero) {
            return true;
        }

        return m_timer.WaitOne(cancellationWaitHandle: null, dueTime: duration);
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_timer.Dispose();
    }
}
