using System.Runtime.ExceptionServices;
using Puck.Abstractions.Waiting;
using Puck.Maths;

namespace Puck.Hosting.Jobs;

/// <summary>
/// The engine's worker threads and the fork that runs a set of fixed partitions across them: the forking thread
/// takes partitions itself, beside every worker, until none is left, then waits for the partitions other threads
/// still hold. Partition indices come from one counter per fork, so which thread runs a partition varies and which
/// elements a partition holds does not.
/// </summary>
/// <remarks>The pool holds <see cref="JobWorkers.Count"/> less one threads, at <see cref="WorkerPriority"/>; a forking
/// thread is the remaining one. Idle workers block on an operating-system semaphore and a forking thread on its fork's event,
/// so nothing spins. Each <see cref="JobClass"/> holds up to <see cref="ForksPerClass"/> forks across the workers at
/// once, so the frame thread's fork and a tick thread's fork run side by side, and so does a fork opened inside
/// another's partition (a parallel wave's tick forking its population): a worker takes its next partition from the
/// highest class with partitions left, and within a class from the fork opened last, which is the deepest a running
/// partition waits on. A fork whose class has every slot in flight, a fork of one partition, and every fork of a
/// serial pool run on the calling thread in partition order. Every fork, wherever it runs, runs every partition and
/// then rethrows the lowest-numbered failing partition's exception, so what a fork completes never depends on the
/// worker count or on which path it took.
/// <para>A fork's owner waits only once every partition of its fork is taken, so every partition it waits for is
/// running on some thread, and every partition not yet taken belongs to a fork whose owner is still taking them: a
/// nested fork can never leave the pool waiting with work no thread will take.</para></remarks>
public sealed class JobPool : IDisposable {
    /// <summary>The dependency of a pending fork.</summary>
    public static readonly WaitReason JoinWait = new("jobs", "fork partitions");
    private static readonly WaitReason IdleWait = new("jobs", "ready partitions");
    private static readonly JobClass[] Classes = Enum.GetValues<JobClass>();
    /// <summary>The scheduling priority of the pool's workers: above the normal priority of the background work
    /// (bakes, decodes, loads, the runtime's thread pool), so a partition the frame or a tick forks and then joins is
    /// never queued behind that work on a loaded machine, and below the frame thread's. A worker with no partition
    /// waits on the pool's semaphore, so the priority takes no time from other programs while the pool is idle.</summary>
    public const ThreadPriority WorkerPriority = ThreadPriority.AboveNormal;
    /// <summary>How many forks of one class may run across the workers at once: one per thread that can be forking at
    /// a time, with room for a fork nested in a partition of another. A fork past it runs inline.</summary>
    public const int ForksPerClass = 32;
    // The open fork: the high 32 bits its generation, the low 32 bits the next unclaimed partition. Closed while a
    // fork is being set up, so a worker that read an earlier generation can never claim a partition of the next.
    private const uint Closed = uint.MaxValue;

    private readonly Thread[] m_threads;
    // Per worker, the class and partition it is running, each one more than its value, packed (PackedIntPair); zero
    // while it runs none.
    private readonly long[] m_workerHeld;
    private readonly Semaphore m_wake = new(initialCount: 0, maximumCount: int.MaxValue);
    // Per class, indexed by the class's value, which is also its rank (a lower value is taken first): the fork slots,
    // each made by the first fork that holds it, and the mask of the slots a fork holds. A fork takes the lowest free
    // slot, so a fork nested in another's partition holds a higher bit, and a worker scans the bits from the highest.
    private readonly ForkSlot?[][] m_slots;
    private readonly int[] m_held;

    // Wake signals released to the semaphore that no worker has taken yet, and the workers counted idle: a worker
    // counts itself idle before its last look for partitions and stops counting once it wakes. A wake signals at most
    // the idle workers no pending signal already serves, so the pending signals never exceed the worker count.
    private int m_pendingWakes;
    private int m_idleWorkers;
    private volatile bool m_stopping;

    /// <summary>Initializes a new instance of the <see cref="JobPool"/> class with one thread less than the host's
    /// worker count, a forking thread being the last.</summary>
    /// <param name="workers">The host's worker-count setting.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workers"/> is <see langword="null"/>.</exception>
    public JobPool(JobWorkers workers) {
        ArgumentNullException.ThrowIfNull(argument: workers);

        Workers = workers;
        m_slots = new ForkSlot?[Classes.Length][];
        m_held = new int[Classes.Length];
        for (var index = 0; (index < m_slots.Length); index++) {
            m_slots[index] = new ForkSlot?[ForksPerClass];
        }
        m_threads = new Thread[(workers.Count - 1)];
        m_workerHeld = new long[m_threads.Length];
        for (var index = 0; (index < m_threads.Length); index++) {
            var workerIndex = index;
            m_threads[index] = new Thread(start: () => Work(workerIndex)) {
                IsBackground = true,
                Name = $"Puck job worker {index}",
                Priority = WorkerPriority,
            };
            m_threads[index].Start();
        }
    }

    /// <summary>Gets the worker-count setting the pool was sized from.</summary>
    public JobWorkers Workers { get; }
    /// <summary>Gets whether every fork runs inline on the calling thread.</summary>
    public bool Serial => (m_threads.Length == 0);
    /// <summary>Gets whether the calling thread is running a partition of a fork, of any pool: a worker's, or the
    /// forking thread's own share. State a fork's partitions read without a lock is written only where this is
    /// <see langword="false"/>, on the thread that forks, so nothing writes it while a fork runs.</summary>
    public static bool InPartition => (PartitionDepth != 0);

    // How many partition bodies the calling thread is inside: a partition may fork again, inline.
    [ThreadStatic] private static int PartitionDepth;
    /// <summary>Gets the wake signals issued to idle workers that no worker has taken yet: at most the pool's worker
    /// thread count however many forks have run, so an idle worker never wakes for a fork that has finished more
    /// than that many times.</summary>
    public int PendingWakes => Volatile.Read(location: ref m_pendingWakes);

    /// <summary>Runs every partition of <paramref name="body"/> and returns once all have finished. The calling
    /// thread runs partitions too.</summary>
    /// <param name="jobClass">The class the fork runs at.</param>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="body">The partitions' work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <remarks>A partition that throws does not stop the others: every partition runs, and then the exception of the
    /// lowest-numbered partition that threw is rethrown here, on the calling thread.</remarks>
    public void Fork(JobClass jobClass, int partitions, IJobPartitions body) {
        var fork = default(ForkOperation);
        HostWaits.ThrowIfPending((Fork(jobClass, partitions, body, ref fork, WaitDeadline.Synchronous()) == WaitOutcome.DeadlinePassed) ? JoinWait : null);
    }

    /// <summary>Starts a fork or resumes its join. Keep the operation and all partition inputs until it settles. A join
    /// may resume on another thread than the one that started the fork (a tick held across wait slices resumes where
    /// its host runs it), one thread at a time: the starting call takes every partition its thread can before it
    /// returns, so a resumed join only waits.</summary>
    /// <param name="jobClass">The class the fork runs at; a resumed join names the class it started with.</param>
    /// <param name="partitions">The partition count; zero or less runs nothing.</param>
    /// <param name="body">The partitions' work.</param>
    /// <param name="fork">The operation: default to start a fork, the value an earlier call left to resume its
    /// join.</param>
    /// <param name="deadline">How long the join may wait for partitions other threads hold.</param>
    /// <returns><see cref="WaitOutcome.Completed"/> once every partition has run, or
    /// <see cref="WaitOutcome.DeadlinePassed"/> while partitions are still running.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="fork"/> no longer owns these partitions.</exception>
    public WaitOutcome Fork(JobClass jobClass, int partitions, IJobPartitions body, ref ForkOperation fork, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: body);
        ObjectDisposedException.ThrowIf(condition: m_stopping, instance: this);

        var rank = (int)jobClass;

        if (fork.Pool is not null) {
            var owned = ReferenceEquals(fork.Pool, this) && (fork.Class == jobClass) &&
                ((Volatile.Read(ref m_held[rank]) & (1 << fork.Slot)) != 0) && (m_slots[rank][fork.Slot] is { } resumed) &&
                (fork.Generation == PackedIntPair.High(Volatile.Read(ref resumed.State))) && ReferenceEquals(body, resumed.Body) && (partitions == resumed.Count);

            if (!owned) {
                throw new InvalidOperationException("The fork operation no longer owns these partitions.");
            }
            return Join(m_slots[rank][fork.Slot]!, ref fork, deadline);
        }
        if (partitions <= 0) {
            return WaitOutcome.Completed;
        }

        var index = (Serial || (partitions == 1)) ? -1 : TryHoldSlot(rank: rank);

        if (index < 0) {
            // The inline path's fault is local to this call, so a fork on a thread that found its class's slots all
            // held never touches a fork in flight.
            var fault = default(PartitionFault);

            for (var partition = 0; (partition < partitions); partition++) {
                fault.Run(body: body, partition: partition);
            }
            fault.Rethrow();
            return WaitOutcome.Completed;
        }

        // Only the fork holding the slot's bit writes the slot, so the first one to hold it makes it; a worker that
        // reads the slot before it is published skips it, and one that reads it before its state opens takes nothing.
        var slot = m_slots[rank][index];

        if (slot is null) {
            slot = new ForkSlot(rank: rank, index: index);
            Volatile.Write(location: ref m_slots[rank][index], value: slot);
        }

        var generation = unchecked(PackedIntPair.High(value: slot.State) + 1);

        _ = Interlocked.Exchange(location1: ref slot.State, value: PackedIntPair.Pack(high: generation, low: unchecked(((int)Closed))));
        slot.Body = body;
        slot.Count = partitions;
        slot.Fault = default;
        Volatile.Write(location: ref slot.Remaining, value: partitions);
        _ = Interlocked.Exchange(location1: ref slot.State, value: PackedIntPair.Pack(high: generation, low: 0));
        fork = new ForkOperation(this, jobClass, index, generation);
        Wake(wanted: (partitions - 1));

        // The owner drains only its own fork, so its join never waits behind another fork's partition.
        while (slot.TryTake(body: out var taken, partition: out var partition)) {
            slot.Run(body: taken, partition: partition);
            if (Interlocked.Decrement(ref slot.Remaining) == 0) {
                return Settle(slot, ref fork);
            }
        }
        return Join(slot, ref fork, deadline);
    }

    // Holds the lowest free slot of a class and answers its index, or -1 when every slot is held.
    private int TryHoldSlot(int rank) {
        while (true) {
            var held = Volatile.Read(location: ref m_held[rank]);
            var free = ~held;

            if (free == 0) {
                return -1;
            }

            var index = System.Numerics.BitOperations.TrailingZeroCount(value: free);

            if (Interlocked.CompareExchange(location1: ref m_held[rank], value: (held | (1 << index)), comparand: held) == held) {
                return index;
            }
        }
    }

    private static WaitOutcome Join(ForkSlot slot, ref ForkOperation fork, WaitDeadline deadline) {
        var outcome = HostWaits.Wait(slot.Done, JoinWait, deadline);
        if (outcome == WaitOutcome.DeadlinePassed) {
            return outcome;
        }
        return Settle(slot, ref fork);
    }

    private static WaitOutcome Settle(ForkSlot slot, ref ForkOperation fork) {
        var pool = fork.Pool!;

        try {
            slot.Body = null;
            fork = default;
            slot.Fault.Rethrow();
        } finally {
            _ = Interlocked.And(location1: ref pool.m_held[slot.Rank], value: ~(1 << slot.Index));
        }
        return WaitOutcome.Completed;
    }

    /// <summary>Names the partition indices workers are executing for one class's fork at the time of the read.</summary>
    /// <param name="jobClass">The class whose partitions to name.</param>
    /// <returns>The held partition indices in ascending order, comma-separated; empty when no worker holds one.</returns>
    public string DescribeHeldPartitions(JobClass jobClass) {
        var held = new List<int>();
        for (var index = 0; index < m_workerHeld.Length; index++) {
            var packed = Volatile.Read(ref m_workerHeld[index]);
            if ((packed != 0) && (PackedIntPair.High(packed) == ((int)jobClass + 1))) { held.Add(PackedIntPair.Low(packed) - 1); }
        }
        held.Sort();
        return string.Join(", ", held);
    }

    /// <summary>The owning caller's handle for a fork whose partitions may still be running.</summary>
    public readonly struct ForkOperation {
        internal ForkOperation(JobPool pool, JobClass jobClass, int slot, int generation) {
            Pool = pool;
            Class = jobClass;
            Slot = slot;
            Generation = generation;
        }

        internal JobPool? Pool { get; }
        internal JobClass Class { get; }
        internal int Slot { get; }
        internal int Generation { get; }
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
        for (var rank = 0; (rank < m_held.Length); rank++) {
            if (Volatile.Read(ref m_held[rank]) != 0) {
                Console.Error.WriteLine("[jobs: leaving the active fork and its handles with the retiring workers]");
                return;
            }
        }
        foreach (var thread in m_threads) {
            thread.Join();
        }
        m_wake.Dispose();
        foreach (var slots in m_slots) {
            foreach (var slot in slots) {
                slot?.Done.Dispose();
            }
        }
    }

    // Signals as many idle workers as this fork can use beyond its own thread, counting only the idle workers no pending
    // signal already serves. Each fork adds its own wake on top of any other fork's, so a fork opened while another
    // class's wake is still pending wakes the workers it needs; and signals never exceed the idle workers, so they never
    // pile up behind forks that already finished.
    private void Wake(int wanted) {
        while (true) {
            var pending = Volatile.Read(location: ref m_pendingWakes);
            var add = Math.Min(val1: wanted, val2: (Volatile.Read(location: ref m_idleWorkers) - pending));

            if (add <= 0) {
                return;
            }
            if (Interlocked.CompareExchange(location1: ref m_pendingWakes, value: (pending + add), comparand: pending) == pending) {
                _ = m_wake.Release(releaseCount: add);
                return;
            }
        }
    }
    // Runs partitions until no fork has one left, each taken from the highest class that has, and within it from the
    // fork that holds the highest slot; the worker that completes a fork's last partition signals its owner.
    private void Drain(int workerIndex) {
        while (TryTakeAny(slot: out var slot, body: out var body, partition: out var partition)) {
            Volatile.Write(ref m_workerHeld[workerIndex], PackedIntPair.Pack(high: (slot.Rank + 1), low: (partition + 1)));
            slot.Run(body: body, partition: partition);
            Volatile.Write(ref m_workerHeld[workerIndex], 0L);
            if (Interlocked.Decrement(ref slot.Remaining) == 0) {
                _ = slot.Done.Set();
            }
        }
    }
    private bool TryTakeAny(out ForkSlot slot, out IJobPartitions body, out int partition) {
        for (var rank = 0; (rank < m_slots.Length); rank++) {
            for (var held = Volatile.Read(location: ref m_held[rank]); (held != 0);) {
                var index = (31 - System.Numerics.BitOperations.LeadingZeroCount(value: (uint)held));

                held &= ~(1 << index);
                if ((Volatile.Read(location: ref m_slots[rank][index]) is { } candidate) && candidate.TryTake(body: out body, partition: out partition)) {
                    slot = candidate;
                    return true;
                }
            }
        }
        slot = null!;
        body = null!;
        partition = -1;
        return false;
    }
    // A worker counts itself idle and only then looks for partitions once more before it waits. A fork publishes its
    // partitions and only then reads the idle count (both through interlocked operations, which order them), so either
    // the fork counts this worker and signals it or this look finds the fork's partitions: no fork's wake is lost. A
    // signal sent to a worker that found work anyway stays pending and wakes it, or another idle worker, once more.
    private void Work(int workerIndex) {
        var consumer = HostWaits.ClaimConsumer(IdleWait);
        while (true) {
            _ = Interlocked.Increment(location: ref m_idleWorkers);
            if (!m_stopping && !HasUntaken()) {
                HostWaits.Idle(m_wake, consumer);
                _ = Interlocked.Decrement(location: ref m_pendingWakes);
            }
            _ = Interlocked.Decrement(location: ref m_idleWorkers);
            if (m_stopping) {
                return;
            }
            Drain(workerIndex);
        }
    }
    private bool HasUntaken() {
        for (var rank = 0; (rank < m_slots.Length); rank++) {
            for (var held = Volatile.Read(location: ref m_held[rank]); (held != 0); held &= (held - 1)) {
                var index = System.Numerics.BitOperations.TrailingZeroCount(value: held);

                if (Volatile.Read(location: ref m_slots[rank][index]) is { HasUntaken: true }) {
                    return true;
                }
            }
        }
        return false;
    }

    // One fork's slot: its class and index, its partitions' counter, the partitions still running, its owner's event
    // and its error.
    private sealed class ForkSlot(int rank, int index) {
        public readonly int Rank = rank;
        public readonly int Index = index;
        public long State = PackedIntPair.Pack(high: 0, low: unchecked((int)Closed));
        public IJobPartitions? Body;
        public int Count;
        public int Remaining;
        public PartitionFault Fault;
        public readonly AutoResetEvent Done = new(initialState: false);
        private readonly Lock m_faultGate = new();

        // Reads the state first: a fork writes its body and count before the exchange that opens its state, so an open
        // state read here sees them.
        public bool HasUntaken {
            get {
                var next = PackedIntPair.Low(value: ((ulong)Volatile.Read(location: ref State)));

                return ((Volatile.Read(location: ref Body) is not null) && (next < (uint)Volatile.Read(location: ref Count)));
            }
        }

        public bool TryTake(out IJobPartitions body, out int partition) {
            while (true) {
                var state = Volatile.Read(location: ref State);
                var next = PackedIntPair.Low(value: ((ulong)state));
                var candidate = Volatile.Read(location: ref Body);
                var count = Volatile.Read(location: ref Count);

                if ((candidate is null) || (next >= (uint)count)) {
                    body = null!;
                    partition = -1;
                    return false;
                }
                if (Interlocked.CompareExchange(location1: ref State, value: (state + 1L), comparand: state) == state) {
                    body = candidate;
                    partition = (int)next;
                    return true;
                }
            }
        }
        public void Run(IJobPartitions body, int partition) {
            if (!PartitionFault.TryRun(body: body, error: out var error, partition: partition)) {
                lock (m_faultGate) {
                    Fault.Record(error: error!, partition: partition);
                }
            }
        }
    }

    // One fork's error policy, shared by the inline and the parallel path: run every partition, keep the exception of
    // the lowest-numbered one that threw, and rethrow it once all have run.
    private struct PartitionFault {
        private ExceptionDispatchInfo? m_error;
        private int m_partition;

        public static bool TryRun(IJobPartitions body, int partition, out Exception? error) {
            PartitionDepth++;
            try {
                body.Run(partition: partition);
                error = null;
                return true;
            } catch (Exception exception) {
                error = exception;
                return false;
            } finally {
                PartitionDepth--;
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
