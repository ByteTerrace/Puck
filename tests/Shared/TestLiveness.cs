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
        using var liveness = new CancellationTokenSource(delay: Bound);
        var token = liveness.Token;

        while (!step()) {
            try {
                if (!(wait?.Invoke(arg: token) ?? false)) {
                    _ = token.WaitHandle.WaitOne(millisecondsTimeout: 1);
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }

            if (token.IsCancellationRequested) {
                Xunit.Assert.Fail(message: $"The condition did not hold within {Bound}: {(reason?.Invoke() ?? "no reason given")}");
            }
        }
    }
}
