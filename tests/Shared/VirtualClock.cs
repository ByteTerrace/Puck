namespace Puck.Testing;

/// <summary>The one test clock: a <see cref="TimeProvider"/> whose time moves only when a test calls
/// <see cref="Advance"/>, so a deadline, a delay, a backoff window, or a validity instant is decided by the test's
/// events and never by wall time. Its timers keep a system timer's contract: each fires at its own due instant, so a
/// callback reads the time it came due and a shorter deadline elapses before a longer one; a zero due time fires at
/// once on the thread pool; and <see cref="ITimer.Change"/> and periodic timers behave as they do on
/// <see cref="TimeProvider.System"/>. <see cref="WhenArmedAsync"/> lets a law wait until the code under test has armed
/// the deadline it is about to expire, rather than guessing when it got there.
/// <para>A timer due inside an <see cref="Advance"/> fires synchronously on the advancing thread, and a continuation
/// it releases may run there or on another thread. A law that reads the clock after a timer fires therefore advances
/// exactly to that timer's due instant and waits for the event the timer causes before advancing again; advancing past
/// several deadlines at once leaves which instant a released continuation observes to the scheduler.</para></summary>
internal sealed class VirtualClock : TimeProvider {
    private static readonly DateTimeOffset DefaultStart = new(
        day: 1,
        hour: 0,
        minute: 0,
        month: 1,
        offset: TimeSpan.Zero,
        second: 0,
        year: 2026
    );

    private readonly Lock m_lock = new();

    private readonly DateTimeOffset m_start;

    private readonly List<VirtualTimer> m_timers = [];
    private TaskCompletionSource m_changed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private long m_now;
    private long m_sequence;

    /// <summary>Initializes a new instance of the <see cref="VirtualClock"/> class reading midnight UTC on
    /// 1 January 2026.</summary>
    public VirtualClock() : this(start: DefaultStart) { }
    /// <summary>Initializes a new instance of the <see cref="VirtualClock"/> class whose <see cref="GetUtcNow"/> reads
    /// <paramref name="start"/> until a test advances it, so a validity window or an expiry is judged against an
    /// instant the law authored.</summary>
    /// <param name="start">The instant the clock reads before its first advance.</param>
    public VirtualClock(DateTimeOffset start) {
        m_start = start;
    }

    /// <summary>Gets how far the clock has been advanced since it was created.</summary>
    public TimeSpan Elapsed {
        get {
            lock (m_lock) {
                return TimeSpan.FromTicks(value: m_now);
            }
        }
    }
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    private void Arm(VirtualTimer timer, TimeSpan dueTime, TimeSpan period) {
        var fireNow = false;

        lock (m_lock) {
            m_timers.Remove(item: timer);
            timer.DueTime = dueTime;
            timer.Period = period;

            if (dueTime == TimeSpan.Zero) {
                fireNow = true;
            } else if (dueTime != Timeout.InfiniteTimeSpan) {
                ArgumentOutOfRangeException.ThrowIfLessThan(
                    other: TimeSpan.Zero,
                    value: dueTime
                );
                timer.DueAt = (m_now + dueTime.Ticks);
                timer.Sequence = m_sequence++;
                m_timers.Add(item: timer);
            }

            PulseLocked();
        }

        if (fireNow) {
            // A zero due time fires at once on the thread pool, as a system timer's does; it is never left for an
            // advance that may not come, and never runs on the thread that armed it.
            ThreadPool.UnsafeQueueUserWorkItem(
                callBack: static timer => timer.Fire(),
                preferLocal: false,
                state: timer
            );
        }
    }
    private void Disarm(VirtualTimer timer) {
        lock (m_lock) {
            if (m_timers.Remove(item: timer)) {
                PulseLocked();
            }
        }
    }
    private void PulseLocked() {
        m_changed.TrySetResult();
        m_changed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, firing every armed timer whose due instant falls
    /// inside the step, earliest first, each at its own instant. A timer armed by a callback during the step fires in
    /// the same step when it comes due inside it.</summary>
    /// <param name="by">The step; zero fires nothing.</param>
    public void Advance(TimeSpan by) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: TimeSpan.Zero,
            value: by
        );

        long target;

        lock (m_lock) {
            target = (m_now + by.Ticks);
        }

        while (true) {
            VirtualTimer? next = null;

            lock (m_lock) {
                foreach (var timer in m_timers) {
                    if (
                        (timer.DueAt <= target) &&
                        ((next is null) || (timer.DueAt < next.DueAt) || ((timer.DueAt == next.DueAt) && (timer.Sequence < next.Sequence)))
                    ) {
                        next = timer;
                    }
                }

                if (next is null) {
                    m_now = target;
                    PulseLocked();

                    return;
                }

                m_now = next.DueAt;
                m_timers.Remove(item: next);

                if ((next.Period != Timeout.InfiniteTimeSpan) && (next.Period > TimeSpan.Zero)) {
                    next.DueAt = (m_now + next.Period.Ticks);
                    next.Sequence = m_sequence++;
                    m_timers.Add(item: next);
                }

                PulseLocked();
            }

            next.Fire();
        }
    }
    /// <summary>Counts the armed timers whose requested due time is <paramref name="dueTime"/>.</summary>
    /// <param name="dueTime">The due time the timer was armed with, relative to the instant it was armed.</param>
    /// <returns>The count.</returns>
    public int Armed(TimeSpan dueTime) {
        lock (m_lock) {
            return m_timers.Count(predicate: timer => (timer.DueTime == dueTime));
        }
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        ArgumentNullException.ThrowIfNull(argument: callback);

        var timer = new VirtualTimer(
            callback: callback,
            clock: this,
            state: state
        );

        Arm(
            dueTime: dueTime,
            period: period,
            timer: timer
        );

        return timer;
    }
    /// <summary>Expires the deadline <paramref name="pending"/> is waiting on: waits until a timer armed with
    /// <paramref name="dueTime"/> exists, advances to one tick short of its due instant, requires
    /// <paramref name="pending"/> to still be running there, and then advances the final tick. The timer must have been
    /// armed at the clock's current instant, and a law whose operation arms and releases earlier timers of the same due
    /// time first waits until the one it means is the one armed. A law that then awaits <paramref name="pending"/> has
    /// shown that the
    /// deadline runs on this clock and fires exactly at its due time, not before.</summary>
    /// <param name="dueTime">The due time the deadline is armed with.</param>
    /// <param name="pending">The operation the deadline ends.</param>
    /// <param name="ct">The test's own cancellation.</param>
    /// <returns>The expiry.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="pending"/> completed before the deadline was armed on
    /// this clock, or before its due instant.</exception>
    public async Task ExpireAsync(TimeSpan dueTime, Task pending, CancellationToken ct) {
        var armed = WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: dueTime
        );

        if (await Task.WhenAny(
            task1: armed,
            task2: pending
        ).ConfigureAwait(continueOnCapturedContext: false) != armed) {
            throw new InvalidOperationException(message: $"the operation completed before its {dueTime} deadline was armed on the clock");
        }

        await armed.ConfigureAwait(continueOnCapturedContext: false);
        Advance(by: (dueTime - TimeSpan.FromTicks(value: 1)));

        if (pending.IsCompleted) {
            throw new InvalidOperationException(message: $"the operation completed before its {dueTime} deadline was due");
        }

        Advance(by: TimeSpan.FromTicks(value: 1));
    }
    public override long GetTimestamp() {
        lock (m_lock) {
            return m_now;
        }
    }
    public override DateTimeOffset GetUtcNow() => (m_start + Elapsed);
    /// <summary>Waits until at least <paramref name="count"/> armed timers carry the requested due time
    /// <paramref name="dueTime"/> — the point at which the code under test has reached the deadline a law is about to
    /// expire. A timer that fired, was disposed, or was re-armed with another due time no longer counts.</summary>
    /// <param name="dueTime">The due time the timers were armed with.</param>
    /// <param name="count">How many such timers must be armed at once.</param>
    /// <param name="ct">The test's own cancellation.</param>
    /// <returns>The wait.</returns>
    public async Task WhenArmedAsync(TimeSpan dueTime, int count, CancellationToken ct) {
        while (true) {
            Task changed;

            lock (m_lock) {
                if (m_timers.Count(predicate: timer => (timer.DueTime == dueTime)) >= count) {
                    return;
                }

                changed = m_changed.Task;
            }

            await changed.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private sealed class VirtualTimer(VirtualClock clock, TimerCallback callback, object? state) : ITimer {
        private int m_disposed;

        public long DueAt { get; set; }
        public TimeSpan DueTime { get; set; }
        public TimeSpan Period { get; set; }
        public long Sequence { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) {
            if (Volatile.Read(location: ref m_disposed) != 0) {
                return false;
            }

            clock.Arm(
                dueTime: dueTime,
                period: period,
                timer: this
            );

            return true;
        }
        public void Dispose() {
            Volatile.Write(
                location: ref m_disposed,
                value: 1
            );
            clock.Disarm(timer: this);
        }
        public ValueTask DisposeAsync() {
            Dispose();

            return ValueTask.CompletedTask;
        }
        public void Fire() {
            if (Volatile.Read(location: ref m_disposed) == 0) {
                callback(state: state);
            }
        }
    }
}
