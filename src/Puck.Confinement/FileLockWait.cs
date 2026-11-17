namespace Puck.Confinement;

/// <summary>The shared wait policy for a lock opener: only a null result is contention; exceptions escape at once.</summary>
public static class FileLockWait {
    /// <summary>Tries an opener until it returns a held lock, the deadline expires, or cancellation fires.</summary>
    /// <param name="open">Attempts the lock without waiting; null means another holder owns it.</param>
    /// <param name="expired">Asked after each unsuccessful attempt; null means no deadline.</param>
    /// <param name="waiting">Called once after the first unsuccessful attempt, before checking the deadline.</param>
    /// <param name="cancellationToken">Cancels the wait, including before the first attempt.</param>
    /// <param name="initialDelayMilliseconds">The first wait, from 1 to 50 milliseconds; successive waits double up to 50.</param>
    /// <returns>The held lock, or null when the deadline expires.</returns>
    /// <exception cref="ArgumentNullException">The opener is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The initial wait is outside 1 to 50 milliseconds.</exception>
    /// <exception cref="OperationCanceledException">Cancellation fires.</exception>
    public static FileStream? Acquire(Func<FileStream?> open, Func<bool>? expired = null, Action? waiting = null, CancellationToken cancellationToken = default, int initialDelayMilliseconds = 50) {
        var attempt = new Attempt(expired: expired, initialDelayMilliseconds: initialDelayMilliseconds, open: open, waiting: waiting);

        while (!attempt.TryFinish(cancellationToken: cancellationToken)) {
            _ = cancellationToken.WaitHandle.WaitOne(millisecondsTimeout: attempt.DelayMilliseconds);
        }
        return attempt.Held;
    }
    /// <summary>Tries an opener with the same policy as <see cref="Acquire"/>, without blocking a thread between attempts.</summary>
    /// <param name="open">Attempts the lock without waiting; null means another holder owns it.</param>
    /// <param name="expired">Asked after each unsuccessful attempt; null means no deadline.</param>
    /// <param name="waiting">Called once after the first unsuccessful attempt, before checking the deadline.</param>
    /// <param name="cancellationToken">Cancels the wait, including before the first attempt.</param>
    /// <returns>The held lock, or null when the deadline expires.</returns>
    /// <exception cref="ArgumentNullException">The opener is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation fires.</exception>
    public static async Task<FileStream?> AcquireAsync(Func<FileStream?> open, Func<bool>? expired = null, Action? waiting = null, CancellationToken cancellationToken = default) {
        var attempt = new Attempt(expired: expired, initialDelayMilliseconds: 50, open: open, waiting: waiting);

        while (!attempt.TryFinish(cancellationToken: cancellationToken)) {
            await Task.Delay(millisecondsDelay: attempt.DelayMilliseconds, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        return attempt.Held;
    }

    // One attempt owns cancellation, opener faults, notification, expiry and backoff; the two waiters only park differently.
    private sealed class Attempt {
        private readonly Func<FileStream?> m_open;
        private readonly Func<bool>? m_expired;
        private readonly Action? m_waiting;

        private bool m_announced;
        private int m_nextDelay;

        public Attempt(Func<FileStream?> open, Func<bool>? expired, Action? waiting, int initialDelayMilliseconds) {
            ArgumentNullException.ThrowIfNull(argument: open);
            ArgumentOutOfRangeException.ThrowIfLessThan(value: initialDelayMilliseconds, other: 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: initialDelayMilliseconds, other: 50);
            m_open = open;
            m_expired = expired;
            m_waiting = waiting;
            m_nextDelay = initialDelayMilliseconds;
        }

        public int DelayMilliseconds { get; private set; }
        public FileStream? Held { get; private set; }

        public bool TryFinish(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Held = m_open();
            if (Held is not null) { return true; }
            if (!m_announced) {
                m_announced = true;
                m_waiting?.Invoke();
            }
            if (m_expired?.Invoke() ?? false) { return true; }
            DelayMilliseconds = m_nextDelay;
            m_nextDelay = Math.Min(val1: 50, val2: (m_nextDelay * 2));
            return false;
        }
    }
}
