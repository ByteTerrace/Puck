namespace Puck.Hosting.Jobs;

/// <summary>
/// A queue of work items that run on the host's thread pool, at most <see cref="Concurrency"/> at once, in the order they
/// came: work that must not wait for a busy pool of its own (a GPU readback whose slot the device waits for) or must not
/// fill one (a burst of blocking reads, a cache's file writes) runs beside it, on threads the host already schedules,
/// without a thread of its own idling between bursts.
/// <para>Each running item drains the queue before its pool item ends, so a burst costs at most
/// <see cref="Concurrency"/> pool items however long it is. An item queued while every runner is busy is taken by one of
/// them; one queued as the last runner leaves starts another.</para>
/// <para>The run delegate handles its own failures: an exception it lets escape is reported by the host's pool as any
/// unhandled pool exception is, which ends the process.</para>
/// </summary>
/// <typeparam name="T">The work item.</typeparam>
public sealed class BoundedWorkQueue<T> {
    private readonly System.Collections.Concurrent.ConcurrentQueue<T> m_waiting = new();
    private readonly Action<T> m_run;
    // A monitor rather than a System.Threading.Lock, which has no Monitor.Wait.
    private readonly object m_idleGate = new();

    private int m_runners;
    // Items queued and not yet finished: the ones waiting and the ones running.
    private int m_unfinished;

    /// <summary>Initializes a new instance of the <see cref="BoundedWorkQueue{T}"/> class.</summary>
    /// <param name="concurrency">The items that run at once at most; at least one.</param>
    /// <param name="run">Runs one item.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrency"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is <see langword="null"/>.</exception>
    public BoundedWorkQueue(int concurrency, Action<T> run) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: concurrency);
        ArgumentNullException.ThrowIfNull(argument: run);

        Concurrency = concurrency;
        m_run = run;
    }

    /// <summary>Creates a queue the process waits for as it exits, up to <paramref name="exitDrain"/>: the writer for
    /// a cache's files, whose writes no caller waits for but which a process that ends must not abandon.</summary>
    /// <param name="concurrency">The items that run at once at most; at least one.</param>
    /// <param name="run">Runs one item.</param>
    /// <param name="exitDrain">The longest the process waits at exit for the items still unfinished.</param>
    /// <returns>The queue.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrency"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is <see langword="null"/>.</exception>
    public static BoundedWorkQueue<T> DrainedAtExit(int concurrency, Action<T> run, TimeSpan exitDrain) {
        var queue = new BoundedWorkQueue<T>(concurrency: concurrency, run: run);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _ = queue.WaitIdle(timeout: exitDrain);
        return queue;
    }

    /// <summary>Gets the items that run at once at most.</summary>
    public int Concurrency { get; }
    /// <summary>Gets the items queued and not yet finished, running ones included.</summary>
    public int Unfinished => Volatile.Read(location: ref m_unfinished);

    /// <summary>Queues an item and starts a runner for it when fewer than <see cref="Concurrency"/> run.</summary>
    /// <param name="item">The item.</param>
    public void Enqueue(T item) {
        _ = Interlocked.Increment(location: ref m_unfinished);
        m_waiting.Enqueue(item: item);
        TryStartRunner();
    }
    /// <summary>Waits until every item queued so far, and any queued meanwhile, has finished, or the timeout passes.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns><see langword="true"/> when nothing is left unfinished.</returns>
    public bool WaitIdle(TimeSpan timeout) {
        var deadline = (Environment.TickCount64 + ((long)timeout.TotalMilliseconds));

        lock (m_idleGate) {
            while (Volatile.Read(location: ref m_unfinished) > 0) {
                var left = (deadline - Environment.TickCount64);

                if ((left <= 0L) || !Monitor.Wait(obj: m_idleGate, millisecondsTimeout: ((int)Math.Min(val1: left, val2: int.MaxValue)))) {
                    return (Volatile.Read(location: ref m_unfinished) == 0);
                }
            }
            return true;
        }
    }

    private void TryStartRunner() {
        while (!m_waiting.IsEmpty) {
            var runners = Volatile.Read(location: ref m_runners);

            if (runners >= Concurrency) { return; }
            if (Interlocked.CompareExchange(comparand: runners, location1: ref m_runners, value: (runners + 1)) != runners) { continue; }
            _ = ThreadPool.UnsafeQueueUserWorkItem(callBack: static queue => queue.Drain(), preferLocal: false, state: this);
            return;
        }
    }
    private void Drain() {
        while (m_waiting.TryDequeue(result: out var item)) {
            try {
                m_run(obj: item);
            } finally {
                if (Interlocked.Decrement(location: ref m_unfinished) == 0) {
                    lock (m_idleGate) { Monitor.PulseAll(obj: m_idleGate); }
                }
            }
        }
        _ = Interlocked.Decrement(location: ref m_runners);
        // An item queued after the last dequeue and before the decrement found every runner busy.
        TryStartRunner();
    }
}
