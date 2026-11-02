using System.Runtime.ExceptionServices;

namespace Puck.Hosting.Jobs;

/// <summary>
/// The engine's worker threads and the fork that runs a set of fixed partitions across them: the forking thread
/// takes partitions itself, beside every worker, until none is left, then waits for the partitions other threads
/// still hold. Partition indices come from one counter, so which thread runs a partition varies and which elements a
/// partition holds does not.
/// </summary>
/// <remarks>The pool holds <see cref="JobWorkers.Count"/> less one threads, at normal priority; the forking thread is
/// the remaining one. Idle workers block on an operating-system semaphore and the forking thread on an event, so
/// nothing spins. One fork runs at a time: a fork requested while another is in flight, a fork of one partition, and
/// every fork of a serial pool run inline on the calling thread, in partition order.</remarks>
public sealed class JobPool : IDisposable {
    // The open fork: the high 32 bits its generation, the low 32 bits the next unclaimed partition. Closed while a
    // fork is being set up, so a worker that read an earlier generation can never claim a partition of the next.
    private const uint Closed = uint.MaxValue;

    private readonly Thread[] m_threads;
    private readonly Semaphore m_wake = new(initialCount: 0, maximumCount: int.MaxValue);
    private readonly AutoResetEvent m_done = new(initialState: false);

    private long m_state = Closed;
    private IJobPartitions? m_body;
    private int m_count;
    private int m_remaining;
    private int m_busy;
    private ExceptionDispatchInfo? m_fault;
    private volatile bool m_stopping;

    /// <summary>Initializes a new instance of the <see cref="JobPool"/> class with one thread less than the host's
    /// worker count, the forking thread being the last.</summary>
    /// <param name="workers">The host's worker-count setting.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workers"/> is <see langword="null"/>.</exception>
    public JobPool(JobWorkers workers) {
        ArgumentNullException.ThrowIfNull(argument: workers);

        Workers = workers;
        m_threads = new Thread[(workers.Count - 1)];
        for (var index = 0; (index < m_threads.Length); index++) {
            m_threads[index] = new Thread(start: Work) {
                IsBackground = true,
                Name = $"Puck job worker {index}",
            };
            m_threads[index].Start();
        }
    }

    /// <summary>Gets the worker-count setting the pool was sized from.</summary>
    public JobWorkers Workers { get; }
    /// <summary>Gets whether every fork runs inline on the calling thread.</summary>
    public bool Serial => (m_threads.Length == 0);

    /// <summary>Runs every partition of <paramref name="body"/> and returns once all have finished. The calling
    /// thread runs partitions too.</summary>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="body">The partitions' work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <remarks>An exception a partition throws is rethrown here, on the calling thread, after every partition has
    /// finished; when several throw, the first recorded is rethrown.</remarks>
    public void Fork(int partitions, IJobPartitions body) {
        ArgumentNullException.ThrowIfNull(argument: body);
        ObjectDisposedException.ThrowIf(condition: m_stopping, instance: this);

        if (partitions <= 0) {
            return;
        }
        if (
            Serial ||
            (partitions == 1) ||
            (Interlocked.CompareExchange(location1: ref m_busy, value: 1, comparand: 0) != 0)
        ) {
            for (var partition = 0; (partition < partitions); partition++) {
                body.Run(partition: partition);
            }
            return;
        }

        try {
            var generation = ((m_state >> 32) + 1L);

            _ = Interlocked.Exchange(location1: ref m_state, value: ((generation << 32) | Closed));
            m_body = body;
            m_count = partitions;
            m_fault = null;
            Volatile.Write(location: ref m_remaining, value: partitions);
            _ = Interlocked.Exchange(location1: ref m_state, value: (generation << 32));
            _ = m_wake.Release(releaseCount: Math.Min(val1: m_threads.Length, val2: (partitions - 1)));

            if (!Drain()) {
                _ = m_done.WaitOne();
            }
            m_body = null;
            m_fault?.Throw();
        } finally {
            Volatile.Write(location: ref m_busy, value: 0);
        }
    }
    /// <summary>Stops and joins every worker thread. A fork in flight on another thread must have returned first.</summary>
    public void Dispose() {
        if (m_stopping) {
            return;
        }

        m_stopping = true;
        if (m_threads.Length > 0) {
            _ = m_wake.Release(releaseCount: m_threads.Length);
        }
        foreach (var thread in m_threads) {
            thread.Join();
        }
        m_wake.Dispose();
        m_done.Dispose();
    }

    // Runs partitions until none is unclaimed; true when this thread finished the fork's last partition.
    private bool Drain() {
        var last = false;

        while (TryTake(body: out var body, partition: out var partition)) {
            last = Complete(body: body, partition: partition);
        }
        return last;
    }
    private bool TryTake(out IJobPartitions body, out int partition) {
        while (true) {
            var state = Volatile.Read(location: ref m_state);
            var next = (uint)state;
            var candidate = Volatile.Read(location: ref m_body);
            var count = Volatile.Read(location: ref m_count);

            if ((candidate is null) || (next >= (uint)count)) {
                body = null!;
                partition = -1;
                return false;
            }
            if (Interlocked.CompareExchange(location1: ref m_state, value: (state + 1L), comparand: state) == state) {
                body = candidate;
                partition = (int)next;
                return true;
            }
        }
    }
    private bool Complete(IJobPartitions body, int partition) {
        try {
            body.Run(partition: partition);
        } catch (Exception exception) {
            _ = Interlocked.CompareExchange(location1: ref m_fault, value: ExceptionDispatchInfo.Capture(source: exception), comparand: null);
        }
        return (Interlocked.Decrement(location: ref m_remaining) == 0);
    }
    private void Work() {
        while (true) {
            _ = m_wake.WaitOne();
            if (m_stopping) {
                return;
            }
            while (TryTake(body: out var body, partition: out var partition)) {
                if (Complete(body: body, partition: partition)) {
                    _ = m_done.Set();
                }
            }
        }
    }
}
