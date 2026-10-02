using System.Diagnostics;

namespace Puck.Testing;

/// <summary>
/// The one rule for a harness waiting on work that runs on the thread pool: block on the work's own completion (a
/// build's <c>WaitFinished</c>, <c>SdfWorldResidency.WaitPipelineBuilds</c>, a signal the work sets, a task it
/// completes) under one generous liveness bound, <see cref="Bound"/>. A loaded machine whose pool is starved reaches the
/// bound only when the work never finishes, so the bound decides nothing. A wait never spins a processor the work needs:
/// a condition with no completion signal is polled under the same bound with a short pause between reads.
/// </summary>
internal static class TestLiveness {
    /// <summary>The liveness bound on every wait for work on the thread pool.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(value: 5);

    /// <summary>Calls <paramref name="step"/> until it returns <see langword="true"/>, failing the law once
    /// <see cref="Bound"/> passes. Between calls it blocks on the work the step waits for through
    /// <paramref name="wait"/>, or, with no wait or no work in flight, pauses a millisecond.</summary>
    /// <param name="step">Takes one step, such as producing a frame, and returns whether the condition holds.</param>
    /// <param name="reason">Describes why the condition does not hold yet, read only when the bound passes.</param>
    /// <param name="wait">Blocks until the work the step waits for finishes, returning whether any was in flight, or
    /// <see langword="null"/> when the condition has no completion signal.</param>
    public static void Until(Func<bool> step, Func<string?>? reason = null, Func<CancellationToken, bool>? wait = null) {
        using var liveness = new CancellationTokenSource();
        using var finished = new ManualResetEventSlim(initialState: false);
        var token = liveness.Token;
        var started = Stopwatch.GetTimestamp();
        // A pool timer cannot enforce a bound on a starved pool. Only a blocking wait needs a watchdog thread;
        // polling checks elapsed time on the calling thread.
        var deadline = ((wait is null) ? Task.CompletedTask : Task.Factory.StartNew(
            action: () => {
                var remaining = (Bound - Stopwatch.GetElapsedTime(startingTimestamp: started));

                if ((remaining <= TimeSpan.Zero) || !finished.Wait(timeout: remaining)) {
                    liveness.Cancel();
                }
            },
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        ));

        try {
            while (!token.IsCancellationRequested && (Stopwatch.GetElapsedTime(startingTimestamp: started) < Bound)) {
                if (step()) {
                    return;
                }

                try {
                    if (!(wait?.Invoke(arg: token) ?? false)) {
                        Thread.Sleep(millisecondsTimeout: 1);
                    }
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            }

            Xunit.Assert.Fail(message: $"The condition did not hold within {Bound}: {(reason?.Invoke() ?? "no reason given")}");
        } finally {
            finished.Set();
            deadline.GetAwaiter().GetResult();
        }
    }
    /// <summary>Calls <paramref name="step"/>, one driven frame each, until it returns <see langword="true"/>, failing the
    /// law once <paramref name="frames"/> frames pass without it. After each frame it waits out the work that frame
    /// started, polling <paramref name="building"/> under <see cref="Bound"/>, so the frame count, not the pool's timing,
    /// decides the law and a condition that never holds fails within those frames.</summary>
    /// <param name="frames">The frames the condition must hold within, each taken with no work left in flight.</param>
    /// <param name="step">Takes one frame and returns whether the condition holds.</param>
    /// <param name="building">Returns whether work the frames wait for is still running on the thread pool.</param>
    /// <param name="reason">Describes why the condition does not hold, read only when the frames run out.</param>
    public static void Within(int frames, Func<bool> step, Func<bool> building, Func<string?>? reason = null) {
        for (var frame = 0; (frame < frames); frame++) {
            if (step()) {
                return;
            }

            Until(
                reason: () => "The work a frame started never finished.",
                step: () => !building()
            );
        }

        Xunit.Assert.Fail(message: $"The condition did not hold within {frames} frames: {(reason?.Invoke() ?? "no reason given")}");
    }
}
