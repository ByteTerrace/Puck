namespace Puck.Abstractions.Jobs;

/// <summary>
/// Runs a fork's partitions across the runtime's thread pool, for background work (a bake's stage) that must not take
/// <c>Puck.Hosting.Jobs.JobPool</c>'s workers from the frame or a tick. It keeps <c>JobPool.Fork</c>'s
/// contract: the calling thread takes partitions itself until none is left and then waits only for the partitions other
/// threads hold, so a pool busy with work queued before the fork never holds the caller behind it (a helper that starts
/// after every partition is taken takes none and returns), and every partition runs before the lowest-numbered failing
/// partition's exception is rethrown. Partition indices come from one counter, so which thread runs a partition varies
/// and what a partition holds does not.
/// </summary>
public static class PoolFork {
    /// <summary>Gets whether the runtime's thread pool holds work queued and not yet started: a fork opened now would
    /// most likely run on its caller alone, so work that only pays when other threads help (a speculation the caller
    /// then checks) is better run serially.</summary>
    public static bool Saturated => (ThreadPool.PendingWorkItemCount > 0);

    /// <summary>Runs every partition of <paramref name="body"/> and returns once all have finished; the calling thread
    /// runs partitions too, and waits on an event, never spinning, for those other threads hold.</summary>
    /// <param name="body">The partitions' work.</param>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="helpers">The most thread-pool threads asked to help, or a negative count for one fewer than the
    /// processors; zero runs every partition on the calling thread, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <remarks>A partition that throws does not stop the others: every partition runs, and then the exception of the
    /// lowest-numbered partition that threw is rethrown here, on the calling thread.</remarks>
    public static void Run(IJobPartitions body, int partitions, int helpers = -1) {
        ArgumentNullException.ThrowIfNull(argument: body);
        if (partitions <= 0) { return; }

        var asked = Math.Min(val1: (partitions - 1), val2: ((helpers < 0) ? (Environment.ProcessorCount - 1) : helpers));
        var fork = new Fork(body: body, count: partitions);

        for (var helper = 0; (helper < asked); helper++) {
            _ = ThreadPool.UnsafeQueueUserWorkItem(callBack: fork, preferLocal: false);
        }
        fork.Take();
        fork.Join();
    }

    /// <summary>Runs <paramref name="body"/> for every partition index, as <see cref="Run(IJobPartitions, int, int)"/>
    /// runs a body's partitions.</summary>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="body">The work of one partition, given its index.</param>
    /// <param name="helpers">The most thread-pool threads asked to help, or a negative count for one fewer than the
    /// processors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public static void Run(int partitions, Action<int> body, int helpers = -1) {
        ArgumentNullException.ThrowIfNull(argument: body);
        Run(body: new Delegated(body: body), helpers: helpers, partitions: partitions);
    }

    // A delegate's partitions.
    private sealed class Delegated(Action<int> body) : IJobPartitions {
        public void Run(int partition) => body(obj: partition);
    }

    // One fork's partitions: the next unclaimed one, how many have finished, the event the last to finish sets, and the
    // lowest-numbered failure. A helper holds the fork past its join only to find no partition left.
    private sealed class Fork(IJobPartitions body, int count) : IThreadPoolWorkItem {
        private readonly ManualResetEventSlim m_finished = new(initialState: false, spinCount: 0);
        private readonly Lock m_failureGate = new();
        private int m_next;
        private int m_done;
        private int m_failedPartition = int.MaxValue;
        private Exception? m_failure;

        public void Execute() => Take();

        // Runs partitions until none is left unclaimed.
        public void Take() {
            for (var partition = (Interlocked.Increment(location: ref m_next) - 1); (partition < count); partition = (Interlocked.Increment(location: ref m_next) - 1)) {
                try {
                    body.Run(partition: partition);
                } catch (Exception exception) {
                    lock (m_failureGate) {
                        if (partition < m_failedPartition) { (m_failedPartition, m_failure) = (partition, exception); }
                    }
                }
                if (Interlocked.Increment(location: ref m_done) == count) { m_finished.Set(); }
            }
        }
        // Waits for the partitions other threads hold, then rethrows the lowest-numbered failure. Only the caller joins,
        // once its own Take found nothing left, so every partition it waits for is running. The event is never disposed:
        // the thread that finished the last partition may still be inside its Set, and an event never asked for its wait
        // handle holds no kernel object.
        public void Join() {
            if (Volatile.Read(location: ref m_done) < count) { m_finished.Wait(); }
            if (m_failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source: failure); }
        }
    }
}
