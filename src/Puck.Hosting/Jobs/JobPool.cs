using System.Runtime.ExceptionServices;
using Puck.Abstractions.Waiting;
using Puck.Maths;

namespace Puck.Hosting.Jobs;

/// <summary>
/// The engine's worker threads and the fork that runs a set of fixed partitions across them: the forking thread
/// takes partitions itself, beside every worker, until none is left, then waits for the partitions other threads
/// still hold. Partition indices come from one counter, so which thread runs a partition varies and which elements a
/// partition holds does not.
/// </summary>
/// <remarks>The pool holds <see cref="JobWorkers.Count"/> less one threads, at normal priority; the forking thread is
/// the remaining one. Idle workers block on an operating-system semaphore and the forking thread on an event, so
/// nothing spins. One fork runs across the workers at a time: a fork requested while another is in flight, a fork of
/// one partition, and every fork of a serial pool run on the calling thread in partition order. Every fork, wherever
/// it runs, runs every partition and then rethrows the lowest-numbered failing partition's exception, so what a fork
/// completes never depends on the worker count or on which path it took.</remarks>
public sealed class JobPool : IDisposable {
    /// <summary>The dependency of a pending fork.</summary>
    public static readonly WaitReason JoinWait = new("jobs", "fork partitions");
    private static readonly WaitReason IdleWait = new("jobs", "ready partitions");
    // The open fork: the high 32 bits its generation, the low 32 bits the next unclaimed partition. Closed while a
    // fork is being set up, so a worker that read an earlier generation can never claim a partition of the next.
    private const uint Closed = uint.MaxValue;

    private readonly Thread[] m_threads;
    private readonly int[] m_workerPartitions;
    private readonly Semaphore m_wake = new(initialCount: 0, maximumCount: int.MaxValue);
    private readonly AutoResetEvent m_done = new(initialState: false);
    private readonly Lock m_faultGate = new();

    private long m_state = Closed;
    private IJobPartitions? m_body;
    private int m_count;
    private int m_remaining;
    private int m_busy;
    // Wake signals released to the semaphore that no worker has taken yet; never more than the worker count.
    private int m_pendingWakes;
    private PartitionFault m_fault;
    private volatile bool m_stopping;

    /// <summary>Initializes a new instance of the <see cref="JobPool"/> class with one thread less than the host's
    /// worker count, the forking thread being the last.</summary>
    /// <param name="workers">The host's worker-count setting.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workers"/> is <see langword="null"/>.</exception>
    public JobPool(JobWorkers workers) {
        ArgumentNullException.ThrowIfNull(argument: workers);

        Workers = workers;
        m_threads = new Thread[(workers.Count - 1)];
        m_workerPartitions = new int[m_threads.Length];
        for (var index = 0; (index < m_threads.Length); index++) {
            var workerIndex = index;
            m_threads[index] = new Thread(start: () => Work(workerIndex)) {
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
    /// <summary>Gets the wake signals issued to idle workers that no worker has taken yet: at most the pool's worker
    /// thread count however many forks have run, so an idle worker never wakes for a fork that has finished more
    /// than that many times.</summary>
    public int PendingWakes => Volatile.Read(location: ref m_pendingWakes);

    /// <summary>Runs every partition of <paramref name="body"/> and returns once all have finished. The calling
    /// thread runs partitions too.</summary>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="body">The partitions' work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <remarks>A partition that throws does not stop the others: every partition runs, and then the exception of the
    /// lowest-numbered partition that threw is rethrown here, on the calling thread.</remarks>
    public void Fork(int partitions, IJobPartitions body) {
        var fork = default(ForkOperation);
        HostWaits.ThrowIfPending((Fork(partitions, body, ref fork, WaitDeadline.Synchronous()) == WaitOutcome.DeadlinePassed) ? JoinWait : null);
    }

    /// <summary>Starts a fork or resumes its join. Keep the operation and all partition inputs until it settles.</summary>
    public WaitOutcome Fork(int partitions, IJobPartitions body, ref ForkOperation fork, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: body);
        ObjectDisposedException.ThrowIf(condition: m_stopping, instance: this);

        if (fork.Pool is not null) {
            if (!ReferenceEquals(fork.Pool, this) || (fork.ThreadId != Environment.CurrentManagedThreadId) ||
                (fork.Generation != PackedIntPair.High(Volatile.Read(ref m_state))) ||
                (Volatile.Read(ref m_busy) == 0) || !ReferenceEquals(body, m_body) || (partitions != m_count)) {
                throw new InvalidOperationException("The fork operation no longer owns these partitions.");
            }
            return Join(ref fork, deadline);
        }
        if (partitions <= 0) {
            return WaitOutcome.Completed;
        }
        if (
            Serial ||
            (partitions == 1) ||
            (Interlocked.CompareExchange(location1: ref m_busy, value: 1, comparand: 0) != 0)
        ) {
            // The inline path's fault is local to this call, so a fork nested in a partition, or one on another
            // thread that found the workers busy, never touches the fork in flight.
            var fault = default(PartitionFault);

            for (var partition = 0; (partition < partitions); partition++) {
                fault.Run(body: body, partition: partition);
            }
            fault.Rethrow();
            return WaitOutcome.Completed;
        }

        var generation = unchecked(PackedIntPair.High(value: m_state) + 1);

        _ = Interlocked.Exchange(location1: ref m_state, value: PackedIntPair.Pack(high: generation, low: unchecked(((int)Closed))));
        m_body = body;
        m_count = partitions;
        m_fault = default;
        Volatile.Write(location: ref m_remaining, value: partitions);
        _ = Interlocked.Exchange(location1: ref m_state, value: PackedIntPair.Pack(high: generation, low: 0));
        fork = new ForkOperation(this, generation, Environment.CurrentManagedThreadId);
        Wake(wanted: (partitions - 1));

        return Drain(workerIndex: -1) ? Settle(ref fork) : Join(ref fork, deadline);
    }

    private WaitOutcome Join(ref ForkOperation fork, WaitDeadline deadline) {
        var outcome = HostWaits.Wait(m_done, JoinWait, deadline);
        if (outcome == WaitOutcome.DeadlinePassed) {
            return outcome;
        }
        return Settle(ref fork);
    }

    private WaitOutcome Settle(ref ForkOperation fork) {
        try {
            m_body = null;
            fork = default;
            m_fault.Rethrow();
        } finally {
            Volatile.Write(location: ref m_busy, value: 0);
        }
        return WaitOutcome.Completed;
    }

    /// <summary>Names the partition indices workers are executing at the time of the read.</summary>
    public string DescribeHeldPartitions() {
        var held = new List<int>();
        for (var index = 0; index < m_workerPartitions.Length; index++) {
            var partition = Volatile.Read(ref m_workerPartitions[index]);
            if (partition != 0) { held.Add(partition - 1); }
        }
        held.Sort();
        return string.Join(", ", held);
    }

    /// <summary>The owning caller's handle for a fork whose partitions may still be running.</summary>
    public readonly struct ForkOperation {
        internal ForkOperation(JobPool pool, int generation, int threadId) {
            Pool = pool;
            Generation = generation;
            ThreadId = threadId;
        }

        internal JobPool? Pool { get; }
        internal int Generation { get; }
        internal int ThreadId { get; }
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
        if (Volatile.Read(ref m_busy) != 0) {
            Console.Error.WriteLine("[jobs: leaving the active fork and its handles with the retiring workers]");
            return;
        }
        foreach (var thread in m_threads) {
            thread.Join();
        }
        m_wake.Dispose();
        m_done.Dispose();
    }

    // Tops the untaken wake signals up to the workers this fork can use, never past the worker count: a signal still
    // pending from an earlier fork serves this one, so signals never pile up behind forks that already finished.
    private void Wake(int wanted) {
        wanted = Math.Min(val1: m_threads.Length, val2: wanted);
        while (true) {
            var pending = Volatile.Read(location: ref m_pendingWakes);
            var add = (wanted - pending);

            if (add <= 0) {
                return;
            }
            if (Interlocked.CompareExchange(location1: ref m_pendingWakes, value: wanted, comparand: pending) == pending) {
                _ = m_wake.Release(releaseCount: add);
                return;
            }
        }
    }
    // The worker that completes the last partition signals the owner. An owner that finishes last settles inline.
    private bool Drain(int workerIndex) {
        while (TryTake(body: out var body, partition: out var partition)) {
            if (workerIndex >= 0) { Volatile.Write(ref m_workerPartitions[workerIndex], partition + 1); }
            RunPartition(body: body, partition: partition);
            if (workerIndex >= 0) { Volatile.Write(ref m_workerPartitions[workerIndex], 0); }
            if (Interlocked.Decrement(ref m_remaining) == 0) {
                if (workerIndex >= 0) { _ = m_done.Set(); }
                return true;
            }
        }
        return false;
    }
    private bool TryTake(out IJobPartitions body, out int partition) {
        while (true) {
            var state = Volatile.Read(location: ref m_state);
            var next = PackedIntPair.Low(value: ((ulong)state));
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
    private void RunPartition(IJobPartitions body, int partition) {
        if (!PartitionFault.TryRun(body: body, error: out var error, partition: partition)) {
            lock (m_faultGate) {
                m_fault.Record(error: error!, partition: partition);
            }
        }
    }
    private void Work(int workerIndex) {
        var consumer = HostWaits.ClaimConsumer(IdleWait);
        while (true) {
            HostWaits.Idle(m_wake, consumer);
            _ = Interlocked.Decrement(location: ref m_pendingWakes);
            if (m_stopping) {
                return;
            }
            _ = Drain(workerIndex);
        }
    }

    // One fork's error policy, shared by the inline and the parallel path: run every partition, keep the exception of
    // the lowest-numbered one that threw, and rethrow it once all have run.
    private struct PartitionFault {
        private ExceptionDispatchInfo? m_error;
        private int m_partition;

        public static bool TryRun(IJobPartitions body, int partition, out Exception? error) {
            try {
                body.Run(partition: partition);
                error = null;
                return true;
            } catch (Exception exception) {
                error = exception;
                return false;
            }
        }
        public void Run(IJobPartitions body, int partition) {
            if (!TryRun(body: body, error: out var error, partition: partition)) {
                Record(error: error!, partition: partition);
            }
        }
        public void Record(Exception error, int partition) {
            if ((m_error is null) || (partition < m_partition)) {
                m_error = ExceptionDispatchInfo.Capture(source: error);
                m_partition = partition;
            }
        }
        public readonly void Rethrow() => m_error?.Throw();
    }
}
