using System.Diagnostics;
using Puck.Abstractions.Pacing;
using Puck.Maths;

namespace Puck.Abstractions.Waiting;

/// <summary>
/// The engine's one door for blocking a thread. A wait on another subsystem's progress takes a deadline and a reason and
/// answers a <see cref="WaitOutcome"/>; only a queue's consumer (<see cref="WaitConsumer"/>) waits without one, and only
/// for its own next work. <c>WAIT001</c> refuses a blocking runtime member anywhere else in a project held to it.
/// <para>
/// The host loop registers its pump thread at each control phase (<see cref="BeginPumpIteration"/>): a deadline wait on
/// that thread is clipped to what the iteration's allowance has left and the time it blocked is charged to it, so
/// sequential waits between two control phases block for at most one allowance in all. A pump thread cannot claim a
/// consumer, and a consumer's thread cannot become a pump.
/// </para>
/// <para>
/// The door keeps each thread's standing deadline wait and the latest expired ones (<see cref="Standing"/>,
/// <see cref="Expired"/>). A wait whose object is already complete returns before any of that and allocates nothing.
/// Waits read the host's monotonic clock (<see cref="Stopwatch"/>); none of this is simulation state.
/// </para>
/// </summary>
public static class HostWaits {
    private const int ExpiredCapacity = 16;

    private static readonly Lock RegistryGate = new();
    private static readonly Lock ExpiredGate = new();
    private static readonly WaitRecord[] ExpiredRing = new WaitRecord[ExpiredCapacity];
    private static ThreadSlot[] Slots = [];
    private static int ExpiredCount;
    private static int ExpiredNext;

    [ThreadStatic]
    private static ThreadSlot? CurrentSlot;

    /// <summary>Gets the pump allowance the calling thread has left, or <see langword="null"/> when it is not the
    /// registered pump.</summary>
    public static TimeSpan? PumpAllowance =>
        ((CurrentSlot is { IsPump: true } slot) ? Stopwatch.GetElapsedTime(startingTimestamp: 0L, endingTimestamp: slot.AllowanceTicks) : null);

    /// <summary>Registers the calling thread as the host loop's pump and opens a fresh allowance for the iteration whose
    /// control phase just ran.</summary>
    /// <param name="allowance">The total the iteration's deadline waits may block for; zero or less lets none block.</param>
    /// <exception cref="InvalidOperationException">The calling thread holds a <see cref="WaitConsumer"/>.</exception>
    public static void BeginPumpIteration(TimeSpan allowance) {
        var slot = Slot();

        if (slot.Consumer is not null) {
            throw new InvalidOperationException(message: $"Thread '{slot.Owner.Name}' is a queue's consumer ({slot.Consumer.Reason}) and cannot be the host loop's pump.");
        }

        slot.IsPump = true;
        slot.AllowanceTicks = WaitDeadline.TicksOf(budget: allowance);
    }
    /// <summary>Ends the calling thread's registration as the pump; a no-op on any other thread.</summary>
    public static void EndPump() {
        if (CurrentSlot is { } slot) {
            slot.IsPump = false;
            slot.AllowanceTicks = 0L;
        }
    }
    /// <summary>Mints the consumer role for the calling thread, or returns the one it already holds.</summary>
    /// <param name="reason">What the consumer waits for while idle.</param>
    /// <returns>The calling thread's consumer.</returns>
    /// <exception cref="InvalidOperationException">The calling thread is the registered pump.</exception>
    public static WaitConsumer ClaimConsumer(in WaitReason reason) {
        var slot = Slot();

        if (slot.IsPump) {
            throw new InvalidOperationException(message: $"The host loop's pump thread cannot wait as a queue's consumer ({reason}); it waits with a deadline.");
        }

        return (slot.Consumer ??= new WaitConsumer(reason: reason, threadId: Environment.CurrentManagedThreadId));
    }

    /// <summary>Waits for <paramref name="handle"/> to signal. A signal taken by the wait is consumed, as
    /// <see cref="WaitHandle.WaitOne()"/> consumes it.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns>Whether the handle signaled before the deadline.</returns>
    public static WaitOutcome Wait(WaitHandle handle, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: handle);

        var wait = new HandleWait(handle: handle);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Waits for <paramref name="signal"/> to be set.</summary>
    /// <param name="signal">The event.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns>Whether the event was set before the deadline.</returns>
    public static WaitOutcome Wait(ManualResetEventSlim signal, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: signal);

        var wait = new SignalWait(signal: signal);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Waits for <paramref name="task"/> to finish. A faulted or canceled task counts as finished; the caller
    /// reads its result or exception afterwards.</summary>
    /// <param name="task">The task.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns>Whether the task finished before the deadline.</returns>
    public static WaitOutcome Wait(Task task, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: task);

        var wait = new TaskWait(task: task);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Takes a permit from <paramref name="semaphore"/>. A wait whose deadline passes took no permit and owes no
    /// release.</summary>
    /// <param name="semaphore">The semaphore.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns><see cref="WaitOutcome.Completed"/> when a permit was taken.</returns>
    public static WaitOutcome Acquire(SemaphoreSlim semaphore, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: semaphore);

        var wait = new PermitWait(semaphore: semaphore);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Waits for <paramref name="monitor"/> to be pulsed. The caller holds the monitor, as
    /// <see cref="Monitor.Wait(object)"/> requires, and re-tests its condition after the wait, whichever way it ended.</summary>
    /// <param name="monitor">The monitor's object, held by the calling thread.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns>Whether the monitor was pulsed before the deadline.</returns>
    /// <exception cref="SynchronizationLockException">The calling thread does not hold the monitor.</exception>
    public static WaitOutcome OnCondition(object monitor, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: monitor);

        var wait = new ConditionWait(monitor: monitor);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Waits for <paramref name="thread"/> to exit.</summary>
    /// <param name="thread">The started thread.</param>
    /// <param name="reason">Why the thread waits.</param>
    /// <param name="deadline">When the wait gives up.</param>
    /// <returns>Whether the thread exited before the deadline.</returns>
    public static WaitOutcome Join(Thread thread, in WaitReason reason, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(argument: thread);

        var wait = new JoinWait(thread: thread);

        return Run(deadline: deadline, reason: in reason, wait: ref wait);
    }
    /// <summary>Sleeps until <paramref name="time"/> reads <paramref name="deadlineTimestamp"/>: the pacer's own wait, on
    /// the host loop's clock. It sleeps on the precision waiter for the whole remainder, or a millisecond at a time where
    /// the host offers none, and re-reads the clock after each sleep, so one that wakes early sleeps again and one that
    /// wakes late ends it. It never spins and never raises the timer resolution. It is not charged to the pump allowance:
    /// the pacer's deadline bounds it.</summary>
    /// <param name="deadlineTimestamp">The deadline, as a <paramref name="time"/> timestamp.</param>
    /// <param name="time">The host loop's clock.</param>
    /// <param name="precisionWaiter">The platform's high-resolution waiter, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    public static void Until(long deadlineTimestamp, TimeProvider time, IPrecisionWaiter? precisionWaiter) {
        ArgumentNullException.ThrowIfNull(argument: time);

        var frequency = time.TimestampFrequency;

        while (true) {
            var remaining = (deadlineTimestamp - time.GetTimestamp());

            if (remaining <= 0L) {
                return;
            }
            if (
                (precisionWaiter is null) ||
                !precisionWaiter.TryWait(duration: TimeSpan.FromSeconds(value: (((double)remaining) / frequency)))
            ) {
                Thread.Sleep(millisecondsTimeout: 1);
            }
        }
    }
    /// <summary>Waits, with no deadline, for <paramref name="monitor"/> to be pulsed: a consumer idle on its own queue.
    /// The caller holds the monitor and re-tests its queue after the wait.</summary>
    /// <param name="monitor">The queue's monitor object, held by the calling thread.</param>
    /// <param name="consumer">The calling thread's consumer role.</param>
    /// <exception cref="InvalidOperationException"><paramref name="consumer"/> belongs to another thread.</exception>
    /// <exception cref="SynchronizationLockException">The calling thread does not hold the monitor.</exception>
    public static void IdleOnCondition(object monitor, WaitConsumer consumer) {
        ArgumentNullException.ThrowIfNull(argument: monitor);
        ThrowUnlessOwned(consumer: consumer);
        _ = Monitor.Wait(obj: monitor);
    }
    /// <summary>Waits, with no deadline, for <paramref name="handle"/> to signal: a consumer idle on its own queue.</summary>
    /// <param name="handle">The handle that signals new work.</param>
    /// <param name="consumer">The calling thread's consumer role.</param>
    /// <exception cref="InvalidOperationException"><paramref name="consumer"/> belongs to another thread.</exception>
    public static void Idle(WaitHandle handle, WaitConsumer consumer) {
        ArgumentNullException.ThrowIfNull(argument: handle);
        ThrowUnlessOwned(consumer: consumer);
        _ = handle.WaitOne();
    }
    /// <summary>Parks the calling thread for the rest of the process: a thread started only to own a resource whose
    /// lifetime is the process's.</summary>
    /// <param name="consumer">The calling thread's consumer role, naming what it owns.</param>
    /// <exception cref="InvalidOperationException"><paramref name="consumer"/> belongs to another thread.</exception>
    public static void Park(WaitConsumer consumer) {
        ThrowUnlessOwned(consumer: consumer);
        Thread.Sleep(timeout: Timeout.InfiniteTimeSpan);
    }

    /// <summary>Copies the deadline waits standing now, one per waiting thread, into <paramref name="destination"/>.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <returns>The number of records written, at most the span's length.</returns>
    public static int Standing(Span<WaitRecord> destination) {
        var slots = Volatile.Read(location: ref Slots);
        var count = 0;

        foreach (var slot in slots) {
            if (count == destination.Length) {
                break;
            }

            lock (slot.Gate) {
                if (slot.Standing) {
                    destination[count++] = slot.Record;
                }
            }
        }

        return count;
    }
    /// <summary>Copies the latest deadline waits that expired, newest first, into <paramref name="destination"/>.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <returns>The number of records written, at most the span's length and at most sixteen.</returns>
    public static int Expired(Span<WaitRecord> destination) {
        lock (ExpiredGate) {
            var count = Math.Min(val1: destination.Length, val2: ExpiredCount);

            for (var index = 0; (index < count); index++) {
                destination[index] = ExpiredRing[(((ExpiredNext - 1 - index) % ExpiredCapacity) + ExpiredCapacity) % ExpiredCapacity];
            }

            return count;
        }
    }

    private static void ThrowUnlessOwned(WaitConsumer consumer) {
        ArgumentNullException.ThrowIfNull(argument: consumer);

        if (consumer.ThreadId != Environment.CurrentManagedThreadId) {
            throw new InvalidOperationException(message: $"The consumer role for '{consumer.Reason}' belongs to thread {consumer.ThreadId}, not to thread {Environment.CurrentManagedThreadId}.");
        }
    }
    private static ThreadSlot Slot() {
        if (CurrentSlot is { } slot) {
            return slot;
        }

        slot = new ThreadSlot(owner: Thread.CurrentThread);
        lock (RegistryGate) {
            var live = 0;
            var current = Slots;

            foreach (var entry in current) {
                live += (entry.Owner.IsAlive ? 1 : 0);
            }

            var next = new ThreadSlot[live + 1];
            var index = 0;

            foreach (var entry in current) {
                if (entry.Owner.IsAlive) {
                    next[index++] = entry;
                }
            }
            next[index] = slot;
            Volatile.Write(location: ref Slots, value: next);
        }

        return (CurrentSlot = slot);
    }
    private static WaitOutcome Run<TWait>(ref TWait wait, in WaitReason reason, WaitDeadline deadline) where TWait : struct, IBlockingWait {
        if (wait.TryNow()) {
            return WaitOutcome.Completed;
        }

        var slot = Slot();
        var start = Stopwatch.GetTimestamp();
        var end = deadline.Timestamp;

        if (slot.IsPump) {
            end = Math.Min(val1: end, val2: start.AddSaturating(right: slot.AllowanceTicks));
        }

        var record = new WaitRecord(
            BudgetTicks: Math.Max(val1: 0L, val2: (end - start)),
            OnPump: slot.IsPump,
            Reason: reason,
            StartTimestamp: start,
            ThreadId: Environment.CurrentManagedThreadId,
            ThreadName: slot.Owner.Name
        );

        lock (slot.Gate) {
            slot.Record = record;
            slot.Standing = true;
        }

        var outcome = WaitOutcome.DeadlinePassed;

        try {
            while (true) {
                var remaining = (end - Stopwatch.GetTimestamp());

                if (remaining <= 0L) {
                    break;
                }
                if (wait.Block(milliseconds: MillisecondsOf(ticks: remaining))) {
                    outcome = WaitOutcome.Completed;
                    break;
                }
            }
        } finally {
            lock (slot.Gate) {
                slot.Standing = false;
            }
            if (slot.IsPump) {
                slot.AllowanceTicks = Math.Max(val1: 0L, val2: (slot.AllowanceTicks - (Stopwatch.GetTimestamp() - start)));
            }
        }

        if (outcome == WaitOutcome.DeadlinePassed) {
            lock (ExpiredGate) {
                ExpiredRing[ExpiredNext] = record;
                ExpiredNext = ((ExpiredNext + 1) % ExpiredCapacity);
                ExpiredCount = Math.Min(val1: ExpiredCapacity, val2: (ExpiredCount + 1));
            }
        }

        return outcome;
    }
    // Rounds up, so a wait never asks the OS to wake before the deadline; the loop re-reads the clock either way.
    private static int MillisecondsOf(long ticks) {
        var milliseconds = (((ticks * 1000.0) / Stopwatch.Frequency) + 0.999999);

        return ((milliseconds >= (int.MaxValue - 1)) ? (int.MaxValue - 1) : Math.Max(val1: 1, val2: (int)milliseconds));
    }

    private sealed class ThreadSlot(Thread owner) {
        public readonly Lock Gate = new();
        public readonly Thread Owner = owner;

        public long AllowanceTicks;
        public WaitConsumer? Consumer;
        public bool IsPump;
        public WaitRecord Record;
        public bool Standing;
    }

    private interface IBlockingWait {
        bool TryNow();
        bool Block(int milliseconds);
    }
    private readonly struct HandleWait(WaitHandle handle) : IBlockingWait {
        public bool TryNow() => handle.WaitOne(millisecondsTimeout: 0);
        public bool Block(int milliseconds) => handle.WaitOne(millisecondsTimeout: milliseconds);
    }
    private readonly struct SignalWait(ManualResetEventSlim signal) : IBlockingWait {
        public bool TryNow() => signal.IsSet;
        public bool Block(int milliseconds) => signal.Wait(millisecondsTimeout: milliseconds);
    }
    private readonly struct TaskWait(Task task) : IBlockingWait {
        public bool TryNow() => task.IsCompleted;
        public bool Block(int milliseconds) {
            try {
                return task.Wait(millisecondsTimeout: milliseconds);
            } catch (AggregateException) {
                return true;
            }
        }
    }
    private readonly struct PermitWait(SemaphoreSlim semaphore) : IBlockingWait {
        public bool TryNow() => semaphore.Wait(millisecondsTimeout: 0);
        public bool Block(int milliseconds) => semaphore.Wait(millisecondsTimeout: milliseconds);
    }
    private readonly struct ConditionWait(object monitor) : IBlockingWait {
        public bool TryNow() => false;
        public bool Block(int milliseconds) => Monitor.Wait(obj: monitor, millisecondsTimeout: milliseconds);
    }
    private readonly struct JoinWait(Thread thread) : IBlockingWait {
        public bool TryNow() => thread.Join(millisecondsTimeout: 0);
        public bool Block(int milliseconds) => thread.Join(millisecondsTimeout: milliseconds);
    }
}
