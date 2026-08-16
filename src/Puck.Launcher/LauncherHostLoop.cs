using System.Diagnostics;
using Puck.Abstractions.Pacing;

namespace Puck.Launcher;

internal static class LauncherHostLoop {
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
    public const int SpinThresholdMilliseconds = 2;

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
}
