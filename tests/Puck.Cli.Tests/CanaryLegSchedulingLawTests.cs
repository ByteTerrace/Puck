using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Covers how the canary runner unwinds its concurrent legs. Every blocking leg here waits on the run's own
/// cancellation token rather than on time, so each law is ordered by signals alone.</summary>
public sealed class CanaryLegSchedulingLawTests {
    private static CanaryCommand.CanaryLegWork Leg(Action run) => new() {
        Discriminating = false,
        ManifestIndex = 0,
        Run = run,
        Weight = 1,
    };

    [Fact]
    public void ALegThatThrowsCancelsTheRunningLegsAndRethrowsOnlyOnceTheyHaveEnded() {
        using var cancellation = new CancellationTokenSource();
        using var running = new ManualResetEventSlim();
        var waiterSawCancellation = false;
        var waiterEnded = false;
        var laterStarted = false;
        var completed = 0;
        var work = new[] {
            Leg(run: () => {
                running.Set();
                cancellation.Token.WaitHandle.WaitOne();
                waiterSawCancellation = cancellation.IsCancellationRequested;
                waiterEnded = true;

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                running.Wait();

                throw new InvalidOperationException(message: "leg failed");
            }),
            Leg(run: () => {
                laterStarted = true;
            }),
        };

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: _ => completed++,
            jobs: 2,
            work: work
        ));

        Assert.Equal(
            expected: "leg failed",
            actual: thrown.Message
        );
        Assert.True(condition: waiterSawCancellation);
        Assert.True(condition: waiterEnded);
        Assert.False(condition: laterStarted);
        Assert.Equal(
            actual: completed,
            expected: 0
        );
    }
    [Fact]
    public void ACancelledRunStartsNothingFurtherAndReportsNothingAfterTheCancellation() {
        using var cancellation = new CancellationTokenSource();
        using var running = new ManualResetEventSlim();
        var laterStarted = false;
        var completed = 0;
        var work = new[] {
            Leg(run: () => {
                running.Set();
                cancellation.Token.WaitHandle.WaitOne();

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                running.Wait();
                cancellation.Cancel();
            }),
            Leg(run: () => {
                laterStarted = true;
            }),
        };

        CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: _ => completed++,
            jobs: 2,
            work: work
        );

        Assert.False(condition: laterStarted);
        Assert.Equal(
            actual: completed,
            expected: 0
        );
    }
    [Fact]
    public void ACompletedCallbackThatThrowsStillWaitsForTheRunningLegs() {
        using var cancellation = new CancellationTokenSource();
        using var firstDone = new ManualResetEventSlim();
        var waiterEnded = false;
        var work = new[] {
            Leg(run: () => {
                firstDone.Wait();
                cancellation.Token.WaitHandle.WaitOne();
                waiterEnded = true;

                throw new OperationCanceledException(token: cancellation.Token);
            }),
            Leg(run: () => {
                firstDone.Set();
            }),
        };

        _ = Assert.Throws<InvalidOperationException>(testCode: () => CanaryCommand.RunLegsConcurrently(
            cancellation: cancellation,
            completed: _ => throw new InvalidOperationException(message: "report failed"),
            jobs: 2,
            work: work
        ));

        Assert.True(condition: waiterEnded);
    }
}
