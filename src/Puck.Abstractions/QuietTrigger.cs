namespace Puck.Abstractions;

/// <summary>
/// Runs an action on the thread pool once the requests for it have gone quiet: <see cref="Request"/> arms it, and it runs
/// once no request has come for the quiet period, or once the oldest request it has not answered has waited the latest
/// delay, whichever comes first, so a steady stream of requests still runs it at a bounded interval rather than never.
/// One run answers every request made before it starts; a request made while it runs arms the next. It rests on the
/// host's timer (<see cref="TimeProvider.CreateTimer"/>) and holds no thread while it waits.
/// <para>
/// Every member is safe on any thread. A run that outlasts the quiet period can overlap the next one, so an action that
/// must not run twice at once serializes itself. The action runs on a timer callback, where an exception it lets escape
/// ends the process: it reports its own failures.
/// </para>
/// </summary>
public sealed class QuietTrigger : IDisposable {
    private readonly Action m_action;
    private readonly Lock m_gate = new();
    private readonly long m_latestTicks;
    private readonly long m_quietTicks;
    private readonly TimeProvider m_time;
    private readonly ITimer m_timer;

    private bool m_armed;
    private bool m_disposed;
    private long m_firstRequest;
    private long m_lastRequest;

    /// <summary>Initializes a new instance of the <see cref="QuietTrigger"/> class.</summary>
    /// <param name="action">The action to run.</param>
    /// <param name="quiet">How long requests must stop before the action runs; positive.</param>
    /// <param name="latest">The longest a request waits for a run, however many follow it; at least
    /// <paramref name="quiet"/>.</param>
    /// <param name="time">The clock and timer the trigger rests on; <see langword="null"/> for
    /// <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quiet"/> is not positive, or
    /// <paramref name="latest"/> is shorter than it.</exception>
    public QuietTrigger(Action action, TimeSpan quiet, TimeSpan latest, TimeProvider? time = null) {
        ArgumentNullException.ThrowIfNull(argument: action);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(other: TimeSpan.Zero, value: quiet);
        ArgumentOutOfRangeException.ThrowIfLessThan(other: quiet, value: latest);

        m_action = action;
        m_time = (time ?? TimeProvider.System);
        m_quietTicks = Ticks(span: quiet);
        m_latestTicks = Ticks(span: latest);
        m_timer = m_time.CreateTimer(
            callback: static state => ((QuietTrigger)state!).Elapsed(),
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan,
            state: this
        );
    }

    /// <summary>Asks for a run: the next one starts once requests have been quiet for the quiet period, or once the
    /// oldest unanswered request has waited the latest delay. Does nothing once the trigger is disposed.</summary>
    public void Request() {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            m_lastRequest = m_time.GetTimestamp();

            if (m_armed) {
                return;
            }

            m_armed = true;
            m_firstRequest = m_lastRequest;
            _ = m_timer.Change(dueTime: Span(ticks: m_quietTicks), period: Timeout.InfiniteTimeSpan);
        }
    }
    /// <summary>Stops the trigger: a run not yet started never starts, and later requests do nothing. A run already
    /// started finishes on its own thread; the caller runs the action itself afterwards when what it owed must not be
    /// lost.</summary>
    /// <returns>Whether a request was still unanswered.</returns>
    public bool Stop() {
        lock (m_gate) {
            var owed = m_armed;

            m_armed = false;
            m_disposed = true;
            _ = m_timer.Change(dueTime: Timeout.InfiniteTimeSpan, period: Timeout.InfiniteTimeSpan);

            return owed;
        }
    }
    /// <summary>Stops the trigger (<see cref="Stop"/>) and releases its timer.</summary>
    public void Dispose() {
        _ = Stop();
        m_timer.Dispose();
    }

    // Runs the action once the quiet period, or the latest delay, has passed since the requests it answers; otherwise
    // waits the rest of whichever ends first.
    private void Elapsed() {
        lock (m_gate) {
            if (m_disposed || !m_armed) {
                return;
            }

            var now = m_time.GetTimestamp();
            var wait = Math.Min(
                val1: ((m_lastRequest + m_quietTicks) - now),
                val2: ((m_firstRequest + m_latestTicks) - now)
            );

            if (wait > 0L) {
                _ = m_timer.Change(dueTime: Span(ticks: wait), period: Timeout.InfiniteTimeSpan);

                return;
            }

            m_armed = false;
        }

        m_action();
    }
    private TimeSpan Span(long ticks) => TimeSpan.FromSeconds(value: (((double)ticks) / m_time.TimestampFrequency));
    private long Ticks(TimeSpan span) => checked((long)(span.TotalSeconds * m_time.TimestampFrequency));
}
