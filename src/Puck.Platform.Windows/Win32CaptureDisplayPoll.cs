namespace Puck.Platform.Windows;

/// <summary>Queues a capture's display check at most once per 100 milliseconds when its consumer polls liveness,
/// including when no captured frames arrive. At most one check is queued or running.</summary>
public sealed class Win32CaptureDisplayPoll {
    private const long IntervalMilliseconds = 100L;

    private readonly Action m_check;
    private readonly Lock m_gate = new();
    private readonly Action<Action> m_queue;

    private long m_nextCheckMilliseconds;
    private bool m_pending;

    /// <summary>Creates a poll whose check and queue are supplied by its owner.</summary>
    /// <param name="check">Checks the display under the owner's resource lifetime and callback gates.</param>
    /// <param name="queue">Queues the action off the consumer thread.</param>
    public Win32CaptureDisplayPoll(Action check, Action<Action> queue) {
        ArgumentNullException.ThrowIfNull(argument: check);
        ArgumentNullException.ThrowIfNull(argument: queue);

        m_check = check;
        m_queue = queue;
    }

    /// <summary>Queues a check if its interval has elapsed and none is pending.</summary>
    /// <param name="milliseconds">The owner's monotonic time, in milliseconds.</param>
    public void Poll(long milliseconds) {
        lock (m_gate) {
            if (m_pending || (milliseconds < m_nextCheckMilliseconds)) {
                return;
            }

            m_nextCheckMilliseconds = (milliseconds + IntervalMilliseconds);
            m_pending = true;
        }

        try {
            m_queue(Run);
        } catch {
            ClearPending();
            throw;
        }
    }

    private void Run() {
        try {
            m_check();
        } finally {
            ClearPending();
        }
    }
    private void ClearPending() {
        lock (m_gate) {
            m_pending = false;
        }
    }
}
