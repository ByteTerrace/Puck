using System.Diagnostics;
using Puck.Abstractions.Pacing;

namespace Puck.Launcher;

internal static class LauncherHostLoop {
    public const int SpinThresholdMilliseconds = 2;
    public const uint TargetUpdateRate = 240U;

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

                if ((precisionWaiter is null) || !precisionWaiter.TryWait(duration: TimeSpan.FromSeconds(value: ((double)sleepTicks / frequency)))) {
                    Thread.Sleep(millisecondsTimeout: 1);
                }
            } else {
                Thread.SpinWait(iterations: 48);
            }
        }
    }
}
