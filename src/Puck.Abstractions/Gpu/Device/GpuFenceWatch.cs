using Puck.Abstractions.Waiting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Turns an armed fence's completion into a host event where the backend cannot register one: Vulkan has no call that
/// sets an event when a <c>VkFence</c> signals, and Direct3D 12's <c>SetEventOnCompletion</c> can refuse. Each watched
/// fence is waited in the driver by a watcher thread of its own (<see cref="GpuBatchedSubmissionFence.WaitNativeFor"/>,
/// a host wait), which sets every event asked of it once the fence completes, its device reports a loss, or it is
/// withdrawn. Fences are waited independently, so one queue's pending submission never hides another's completion,
/// whatever order the watches were asked in. A second watch of a fence already watched joins it rather than taking a
/// thread. Watcher threads are started as more fences are outstanding at once than threads are idle, and an idle one
/// parks on a monitor for the next watch.
/// <para>Invariant: no fence is reset or destroyed while a watcher is inside its native wait, which Vulkan requires of
/// <c>vkResetFences</c> and <c>vkDestroyFence</c> and Direct3D 12 of a fence's release. <see cref="Withdraw"/> is the one
/// door, and <see cref="GpuBatchedSubmissionFence"/> calls it before its drain and its disposal, the only places a
/// backend resets or releases its native fence; it returns only once no watcher is inside the wait for that fence. A
/// watcher waits in slices of <see cref="Slice"/>, so a withdrawn fence that never signals is given up within one.</para>
/// </summary>
internal static class GpuFenceWatch {
    /// <summary>The longest one native wait of a watcher blocks before it reads whether its fence was withdrawn: a tenth
    /// of a second.</summary>
    internal static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(value: 100);

    private static readonly WaitReason IdleReason = new(Subsystem: "gpu", WaitsFor: "a fence to watch");
    private static readonly object Gate = new();
    // Watches no watcher has taken yet, and those a watcher is inside the native wait for. Under Gate.
    private static readonly Queue<Watch> Pending = new();
    private static readonly List<Watch> Waiting = [];
    // Watcher threads parked for the next watch. Under Gate.
    private static int Idle;

    /// <summary>Sets <paramref name="signal"/> once <paramref name="fence"/>'s armed submission completes, its device
    /// reports a loss, or the fence is withdrawn. Never blocks.</summary>
    /// <param name="fence">The armed fence.</param>
    /// <param name="signal">The event to set.</param>
    internal static void Begin(GpuBatchedSubmissionFence fence, EventWaitHandle signal) {
        lock (Gate) {
            if (Find(fence: fence) is { } watch) {
                watch.Signals.Add(item: signal);

                return;
            }

            Pending.Enqueue(item: new Watch(fence: fence, signal: signal));
            if (Pending.Count > Idle) {
                new Thread(start: Work) { IsBackground = true, Name = "Puck GPU fence watch" }.Start();
            }
            Monitor.PulseAll(obj: Gate);
        }
    }
    /// <summary>Ends every watch of <paramref name="fence"/> and returns once no watcher is inside its native wait for
    /// it, so the caller may reset or release the native fence. A fence that has signaled, the usual case, releases its
    /// watcher at once.</summary>
    /// <param name="fence">The fence about to be reset or released.</param>
    internal static void Withdraw(GpuBatchedSubmissionFence fence) {
        lock (Gate) {
            foreach (var watch in Pending) {
                if (ReferenceEquals(objA: watch.Fence, objB: fence)) { watch.Withdrawn = true; }
            }
            while (Waited(fence: fence) is { } waited) {
                waited.Withdrawn = true;
                // The watcher pulses as it leaves each watch, within one slice of the withdrawal at the latest. A raw
                // monitor wait, bounded by that slice, rather than the door's: a pump thread whose allowance is spent
                // would otherwise return from every wait at once and spin until the watcher left.
                _ = Monitor.Wait(obj: Gate, timeout: Slice);
            }
        }
    }

    // A watch of the fence that will still set its events, pending or waited.
    private static Watch? Find(GpuBatchedSubmissionFence fence) {
        foreach (var watch in Pending) {
            if (ReferenceEquals(objA: watch.Fence, objB: fence) && !watch.Withdrawn) { return watch; }
        }

        return ((Waited(fence: fence) is { Withdrawn: false } waited) ? waited : null);
    }
    private static Watch? Waited(GpuBatchedSubmissionFence fence) {
        foreach (var watch in Waiting) {
            if (ReferenceEquals(objA: watch.Fence, objB: fence)) { return watch; }
        }

        return null;
    }
    private static void Work() {
        var consumer = HostWaits.ClaimConsumer(reason: IdleReason);
        Watch? done = null;

        while (true) {
            Watch watch;

            lock (Gate) {
                if (done is not null) {
                    _ = Waiting.Remove(item: done);
                    // Set on completion, on a reported loss and on withdrawal alike: whoever wakes reads the fence.
                    // Under Gate, so no signal joins the watch after it was set.
                    foreach (var signal in done.Signals) { _ = signal.Set(); }
                    done = null;
                    Monitor.PulseAll(obj: Gate);
                }
                while (!Pending.TryDequeue(result: out watch!)) {
                    Idle++;
                    try {
                        HostWaits.IdleOnCondition(consumer: consumer, monitor: Gate);
                    } finally {
                        Idle--;
                    }
                }
                if (watch.Withdrawn) { continue; }
                Waiting.Add(item: watch);
            }

            while (true) {
                bool reached;

                try {
                    reached = watch.Fence.WaitNativeFor(slice: Slice);
                } catch {
                    // A failed wait (a lost device) ends the watch: the woken waiter reads the fence and meets it.
                    reached = true;
                }
                if (reached || Volatile.Read(location: ref watch.Withdrawn)) { break; }
            }
            done = watch;
        }
    }

    private sealed class Watch(GpuBatchedSubmissionFence fence, EventWaitHandle signal) {
        public readonly GpuBatchedSubmissionFence Fence = fence;
        public readonly List<EventWaitHandle> Signals = [signal];
        public bool Withdrawn;
    }
}
