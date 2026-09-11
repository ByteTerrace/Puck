using Puck.Abstractions.Machines;

namespace Puck.GamingBricks;

/// <summary>The work item a <see cref="QueuedWorkerLifecycle{TWorkItem}"/> can build for itself. The rest of an
/// item's case set — steps, barriers a caller builds, marshaled requests — belongs to the owning type; the lifecycle
/// only needs the two markers it enqueues on its own behalf and the handle it must release on every abandoned
/// item.</summary>
/// <typeparam name="TSelf">The implementing item type.</typeparam>
public interface IQueuedWorkItem<TSelf> where TSelf : IQueuedWorkItem<TSelf> {
    /// <summary>Gets the handle a producer waits on, or <see langword="null"/> for an item nobody waits for.</summary>
    ManualResetEventSlim? Completion { get; }

    /// <summary>Creates the ordered barrier item whose completion means every earlier item has run.</summary>
    /// <param name="completion">The handle to set once the item is reached.</param>
    /// <returns>The barrier item.</returns>
    static abstract TSelf Barrier(ManualResetEventSlim completion);
    /// <summary>Creates the ordered stop item that ends the worker loop once every earlier item has run.</summary>
    /// <param name="completion">The handle to set once the item is reached.</param>
    /// <returns>The stop item.</returns>
    static abstract TSelf Stop(ManualResetEventSlim completion);
}
/// <summary>
/// The queued-worker lifecycle both the single-machine worker and the cable-link group run on: one owning thread, a
/// FIFO of work items behind a condition variable, a finite pending-step window with producer backpressure, and the
/// fault that closes the queue and releases every waiter rather than stranding one.
/// <para>
/// The ordering is the contract. A stop closes the queue and pulses every backpressured producer awake BEFORE it
/// appends its own ordered marker, so a producer waiting for capacity leaves through the closed-queue path instead of
/// waiting behind a queue nothing will drain; the marker is appended (not jumped ahead of), so the worker finishes
/// every already-accepted item before acknowledging shutdown and the thread is joined only after that acknowledgement.
/// A faulted worker sets the item it died on, then closes the queue and sets every abandoned item's handle under the
/// lock, so no producer waits on work that will never run. A queue that already faulted is never given a stop marker
/// to wait for, because no thread is left to reach it.
/// </para>
/// </summary>
/// <typeparam name="TWorkItem">The owning type's work item.</typeparam>
public sealed class QueuedWorkerLifecycle<TWorkItem> where TWorkItem : IQueuedWorkItem<TWorkItem> {
    private readonly int m_maximumPendingSteps;
    private readonly string m_role;
    private readonly Queue<TWorkItem> m_work;
    private readonly string m_workerName;

    private bool m_acceptingWork;
    private long m_backpressureEvents;
    private long m_completedSteps;
    private long m_submittedSteps;
    private Thread? m_worker;
    private Exception? m_workerFault;

    // Condition variable, not a plain gate: Monitor.Wait/Pulse require an object monitor, which System.Threading.Lock
    // refuses (CS9216).
    private readonly object m_workLock = new();

    /// <summary>Creates a lifecycle for a worker that is not yet running.</summary>
    /// <param name="maximumPendingSteps">The finite number of accepted-but-incomplete step items before
    /// <see cref="Submit"/> applies producer backpressure.</param>
    /// <param name="workerName">The thread's diagnostic name, which also names it in fault text.</param>
    /// <param name="role">The noun naming the thread in fault text, such as <c>"worker"</c> or
    /// <c>"link thread"</c>.</param>
    public QueuedWorkerLifecycle(int maximumPendingSteps, string workerName, string role) {
        m_maximumPendingSteps = maximumPendingSteps;
        m_role = role;
        m_work = new Queue<TWorkItem>(capacity: (maximumPendingSteps + 1));
        m_workerName = workerName;
    }

    /// <summary>Gets the number of submissions that encountered a full pending window and waited for capacity.</summary>
    public long BackpressureEvents {
        get {
            lock (m_workLock) {
                return m_backpressureEvents;
            }
        }
    }
    /// <summary>Gets the number of accepted step items whose work has completed.</summary>
    public long CompletedSteps {
        get {
            lock (m_workLock) {
                return m_completedSteps;
            }
        }
    }
    /// <summary>Gets a fault description, or <see langword="null"/> while the queue is healthy.</summary>
    public string? Fault {
        get {
            lock (m_workLock) {
                return ((m_workerFault is { } fault)
                    ? $"{fault.GetType().Name}: {fault.Message}"
                    : null
                );
            }
        }
    }
    /// <summary>Gets the number of accepted step items not yet completed, including one currently executing.</summary>
    public long PendingSteps {
        get {
            lock (m_workLock) {
                return Math.Max(
                    val1: 0L,
                    val2: (m_submittedSteps - m_completedSteps)
                );
            }
        }
    }
    /// <summary>Gets the owning thread, or <see langword="null"/> while no worker is running.</summary>
    public Thread? Worker =>
        m_worker;

    /// <summary>Records one completed step item and wakes every producer waiting for pending-window capacity. Call
    /// from the thread that ran the item.</summary>
    public void CompleteStep() {
        lock (m_workLock) {
            ++m_completedSteps;
            Monitor.PulseAll(obj: m_workLock);
        }
    }
    /// <summary>Appends a barrier and blocks until the worker has run every item accepted before it — the
    /// submit-and-drain half of a synchronous step. A closed or faulted queue returns without waiting, rethrowing the
    /// fault when there is one.</summary>
    /// <exception cref="InvalidOperationException">The worker faulted.</exception>
    public void Drain() {
        using var completion = new ManualResetEventSlim(initialState: false);

        lock (m_workLock) {
            if (
                (m_workerFault is not null) ||
                !m_acceptingWork
            ) {
                ThrowIfFaultedLocked();

                return;
            }

            m_work.Enqueue(item: TWorkItem.Barrier(completion: completion));
            Monitor.Pulse(obj: m_workLock);
        }

        completion.Wait();
        ThrowIfFaulted();
    }
    /// <summary>Submits one completion-bearing item and blocks until the worker has run it. A refused item (the queue
    /// is closed, or the worker already faulted) never ran, so each caller decides its own fallback. The caller owns
    /// the completion handle's lifetime.</summary>
    /// <param name="item">The item to run on the worker thread.</param>
    /// <returns><see langword="true"/> when the item was accepted and has now run.</returns>
    public bool EnqueueAndWait(in TWorkItem item) {
        var queued = false;

        lock (m_workLock) {
            if (
                m_acceptingWork &&
                (m_workerFault is null)
            ) {
                m_work.Enqueue(item: item);
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            item.Completion?.Wait();
        }

        return queued;
    }
    /// <summary>Records the exception that ended the worker loop: releases the item it died on, closes the queue,
    /// releases every abandoned item's waiter, and wakes every backpressured producer. Call from the worker thread's
    /// own catch.</summary>
    /// <param name="exception">The exception that ended the loop.</param>
    /// <param name="current">The item the loop was running.</param>
    public void FaultWith(Exception exception, in TWorkItem current) {
        current.Completion?.Set();

        lock (m_workLock) {
            m_workerFault = exception;
            m_acceptingWork = false;

            while (m_work.TryDequeue(result: out var abandoned)) {
                abandoned.Completion?.Set();
            }

            Monitor.PulseAll(obj: m_workLock);
        }

        Console.Error.WriteLine(value: $"[{m_workerName}] {m_role} stopped ({exception.GetType().Name}: {exception.Message})");
    }
    /// <summary>Opens an empty queue and starts the owning thread on <paramref name="body"/>.</summary>
    /// <param name="body">The worker loop.</param>
    /// <param name="resetCounters">When <see langword="true"/>, zeroes the step and backpressure counters; when
    /// <see langword="false"/>, keeps the completed count and reopens the pending window empty against it, so a count
    /// a consumer already reads survives a stop/start rather than appearing to restart.</param>
    public void Start(ThreadStart body, bool resetCounters = true) {
        lock (m_workLock) {
            m_work.Clear();
            m_workerFault = null;

            if (resetCounters) {
                m_submittedSteps = 0L;
                m_completedSteps = 0L;
                m_backpressureEvents = 0L;
            } else {
                m_submittedSteps = m_completedSteps;
            }

            m_acceptingWork = true;
        }

        m_worker = new Thread(start: body) {
            IsBackground = true,
            Name = m_workerName,
        };
        m_worker.Start();
    }
    /// <summary>Closes the queue, appends an ordered stop marker so the worker drains every already-accepted item
    /// before acknowledging shutdown, and joins the thread. A no-op when no worker is running.</summary>
    public void Stop() {
        var worker = m_worker;

        if (worker is null) {
            return;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            m_acceptingWork = false;
            Monitor.PulseAll(obj: m_workLock);

            if (m_workerFault is null) {
                m_work.Enqueue(item: TWorkItem.Stop(completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }

        worker.Join();
        m_worker = null;
    }
    /// <summary>Accepts one step item for ordered execution, blocking the producer while the pending window is full
    /// rather than dropping or coalescing authoritative history.</summary>
    /// <param name="item">The step item to accept.</param>
    /// <returns>The observable submission outcome.</returns>
    public QueuedMachineSubmission Submit(in TWorkItem item) {
        var backpressured = false;

        lock (m_workLock) {
            while (
                m_acceptingWork &&
                (m_workerFault is null) &&
                ((m_submittedSteps - m_completedSteps) >= m_maximumPendingSteps)
            ) {
                if (!backpressured) {
                    backpressured = true;

                    if (m_backpressureEvents < long.MaxValue) {
                        ++m_backpressureEvents;
                    }
                }

                Monitor.Wait(obj: m_workLock);
            }

            if (
                !m_acceptingWork ||
                (m_workerFault is not null)
            ) {
                return QueuedMachineSubmission.Rejected;
            }

            m_work.Enqueue(item: item);
            ++m_submittedSteps;
            Monitor.Pulse(obj: m_workLock);
        }

        return (backpressured
            ? QueuedMachineSubmission.AcceptedAfterBackpressure
            : QueuedMachineSubmission.Accepted
        );
    }
    /// <summary>Blocks the worker thread until an item is available and returns it.</summary>
    /// <returns>The next item in FIFO order.</returns>
    public TWorkItem TakeWork() {
        lock (m_workLock) {
            while (m_work.Count == 0) {
                Monitor.Wait(obj: m_workLock);
            }

            return m_work.Dequeue();
        }
    }
    /// <summary>Rethrows the worker's fault, wrapped, when there is one.</summary>
    /// <exception cref="InvalidOperationException">The worker faulted.</exception>
    public void ThrowIfFaulted() {
        lock (m_workLock) {
            ThrowIfFaultedLocked();
        }
    }

    private void ThrowIfFaultedLocked() {
        if (m_workerFault is { } fault) {
            throw new InvalidOperationException(
                innerException: fault,
                message: $"The {m_workerName} {m_role} faulted."
            );
        }
    }
}
