using System.Diagnostics;
using Puck.Abstractions.Pacing;
using Puck.Hosting;

namespace Puck.Launcher;

internal static class LauncherHostLoop {
    // Deliberate busy waiting is limited to the final 100 us. The previous 2 ms threshold burned roughly 120 ms of
    // one CPU core per second at 60 Hz before doing useful work, which is especially costly when a laptop CPU and
    // integrated GPU share the same thermal/power envelope. The precision waiter handles the bulk of the interval;
    // this small tail only absorbs scheduler/timer granularity without becoming a standing frame tax.
    private const long SpinThresholdMicroseconds = 100L;

    /// <summary>The host's OWN pacing default — used when no <see cref="Puck.Hosting.IFixedStepSimulation"/> is
    /// registered (a composition root that drives no fixed-step sim at all, so nothing declares a rate via
    /// <see cref="Puck.Hosting.IFixedStepSimulation.RatePerSecond"/>), AND as the fixed-step pump's own calling
    /// cadence when a registered simulation's <see cref="Puck.Hosting.IFixedStepSimulation.RatePerSecond"/> reports
    /// 0 (a world whose authored <c>simulation.rateHz</c> is the durable stop) — <see cref="Puck.Hosting.EngineTicks.PerRate"/>
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
    public static long SpinThreshold(long frequency) => Math.Max(
        val1: 1L,
        val2: ((frequency * SpinThresholdMicroseconds) / 1_000_000L)
    );
    public static void WaitUntil(long deadlineTimestamp, long frequency, long spinThreshold, IPrecisionWaiter? precisionWaiter) {
        while (true) {
            var remaining = (deadlineTimestamp - Stopwatch.GetTimestamp());

            if (remaining <= 0L) {
                break;
            }

            if (remaining > spinThreshold) {
                var sleepTicks = (remaining - spinThreshold);

                if (
                    (precisionWaiter is null) ||
                    !precisionWaiter.TryWait(duration: TimeSpan.FromSeconds(value: (((double)sleepTicks) / frequency)))
                ) {
                    Thread.Sleep(millisecondsTimeout: 1);
                }
            } else {
                Thread.SpinWait(iterations: 48);
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
    public static Task RunPump(Action pump, string name) {
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
            name: name
        );

        return completion.Task;
    }
    public static Thread StartBackgroundThread(Action action, string name) {
        var thread = new Thread(start: () => action()) {
            IsBackground = true,
            Name = name,
        };

        thread.Start();

        return thread;
    }
}
