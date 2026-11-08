using System.Diagnostics;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class SuiteBudgetLawTests {
    [Fact]
    public void OverlappingPausesResumeOnlyOnceAfterTheLastScope() {
        var clock = Stopwatch.StartNew();
        using var budget = new SuiteBudget(total: TimeSpan.FromMinutes(minutes: 1), clock: clock);
        var first = budget.Pause();
        var second = budget.Pause();
        var remaining = budget.Remaining;
        Assert.False(condition: clock.IsRunning);
        first.Dispose();
        first.Dispose();
        Assert.False(condition: clock.IsRunning);
        Assert.Equal(expected: remaining, actual: budget.Remaining);
        second.Dispose();
        Assert.True(condition: clock.IsRunning);
    }

    [Fact]
    public void PausingPreservesAStoppedClock() {
        var clock = new Stopwatch();
        using var budget = new SuiteBudget(total: TimeSpan.FromMinutes(minutes: 1), clock: clock);
        budget.Pause().Dispose();
        Assert.False(condition: clock.IsRunning);
    }

    [Fact]
    public void CallerCancellationStillEndsAPausedBudget() {
        using var caller = new CancellationTokenSource();
        using var budget = new SuiteBudget(total: TimeSpan.FromMinutes(minutes: 1), caller: caller.Token);
        using var pause = budget.Pause();
        caller.Cancel();
        Assert.True(condition: budget.Expired);
    }

    [Fact]
    public void PausingAnElapsedBudgetCannotReviveIt() {
        using var budget = new SuiteBudget(total: TimeSpan.Zero);
        using var pause = budget.Pause();
        Assert.True(condition: budget.Expired);
        Assert.Equal(expected: TimeSpan.Zero, actual: budget.Remaining);
    }
}
