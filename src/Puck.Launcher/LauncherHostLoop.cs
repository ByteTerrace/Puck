using Puck.Abstractions.Pacing;
using Puck.Hosting;

namespace Puck.Launcher;

internal static class LauncherHostLoop {
    /// <summary>The host's OWN pacing default — used when no <see cref="Puck.Hosting.IFixedStepSimulation"/> is
    /// registered (a composition root that drives no fixed-step sim at all, so nothing declares a rate via
    /// <see cref="Puck.Hosting.IFixedStepSimulation.RatePerSecond"/>), AND as the fixed-step pump's own calling
    /// cadence when a registered simulation's <see cref="Puck.Hosting.IFixedStepSimulation.RatePerSecond"/> reports
    /// 0 (a world whose authored <c>simulation.rateHz</c> is the durable stop) — <see cref="Puck.Maths.FixedTickConversion.PerRate"/>
    /// refuses zero outright (there is no step period for a simulation that never steps), so the PUMP'S OWN cadence,
    /// which is presentation-adjacent host pacing and never simulation state, must never be derived from a rate that
    /// can legitimately be zero. <c>Puck.Launcher</c> is a domain-agnostic generic host and owns no notion of "the"
    /// simulation rate — a registered simulation (e.g. a loaded world document) is what actually declares one; this
    /// is the fallback for both the null and the stopped case, never a value that silently overrides an authored
    /// one (the registered simulation still gates its OWN actual stepping internally).</summary>
    public const uint DefaultUpdateRate = 240U;

    public static T? SingleOrDefault<T>(IEnumerable<T> items, string name, string hostDescription)
        where T : class {
        using var enumerator = items.GetEnumerator();

        if (!enumerator.MoveNext()) {
            return null;
        }

        var item = enumerator.Current;

        if (enumerator.MoveNext()) {
            throw new InvalidOperationException(message: $"The {hostDescription} accepts at most one {name}.");
        }

        return item;
    }
    // Waits until the host clock reads the deadline by sleeping on the host: the precision waiter (a high-resolution
    // waitable timer) for the whole remainder, or a one-millisecond sleep where the host offers none. The loop re-reads
    // the clock after each wait, so a wait that wakes early waits again and one that wakes late ends it. Every read is of
    // `time`, the host loop's own clock, so the wait and the deadline share one clock.
    public static void WaitUntil(TimeProvider time, long deadlineTimestamp, IPrecisionWaiter? precisionWaiter) {
        var frequency = time.TimestampFrequency;

        while (true) {
            var remaining = (deadlineTimestamp - time.GetTimestamp());

            if (remaining <= 0L) {
                break;
            }

            if (
                (precisionWaiter is null) ||
                !precisionWaiter.TryWait(duration: TimeSpan.FromSeconds(value: (((double)remaining) / frequency)))
            ) {
                Thread.Sleep(millisecondsTimeout: 1);
            }
        }
    }
    public static uint ResolveRatePerSecond(IFixedStepSimulation? simulation) {
        var simRatePerSecond = (simulation?.RatePerSecond ?? DefaultUpdateRate);

        return ((simRatePerSecond == 0U)
            ? DefaultUpdateRate
            : simRatePerSecond
        );
    }
    // Runs a host pump on its own dedicated background thread and returns the task a hosted service hands back from
    // ExecuteAsync. The task completes when the pump returns and faults with whatever it threw, so a pump that fails
    // (a device that never comes up, a render root that throws) faults its hosted service and stops the host with the
    // exception in hand, where an unhandled exception on a raw thread would end the process before anything reports.
    // The pump must return once its stopping token is cancelled: the host's StopAsync waits for this task.
    public static Task RunPump(Action pump, string name, ThreadPriority priority = ThreadPriority.Normal) {
        var completion = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        _ = StartBackgroundThread(
            action: () => {
                try {
                    pump();
                    completion.SetResult();
                } catch (Exception exception) {
                    completion.SetException(exception: exception);
                }
            },
            name: name,
            priority: priority
        );

        return completion.Task;
    }
    public static Thread StartBackgroundThread(Action action, string name, ThreadPriority priority = ThreadPriority.Normal) {
        var thread = new Thread(start: () => action()) {
            IsBackground = true,
            Name = name,
            Priority = priority,
        };

        thread.Start();

        return thread;
    }
}
