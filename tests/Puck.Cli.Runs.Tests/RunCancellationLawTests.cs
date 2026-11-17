using System.CommandLine.Invocation;
using System.Diagnostics;
using Puck.Cli.Counters;
using Puck.Cli.Motion;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>Long-running CLI actions carry caller cancellation to their existing waits and exit guard.</summary>
public sealed class RunCancellationLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task MotionAndCountersUseTheCancellableActionGuard(bool motion) {
        var command = (motion ? MotionCommand.Create() : CountersCommand.Create());

        Assert.IsAssignableFrom<AsynchronousCommandLineAction>(@object: command.Action);
        CliExit.Guard(command: command);
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        var exit = await command.Parse(args: (motion ? ["--list"] : [])).InvokeAsync(cancellationToken: cancellation.Token);

        Assert.Equal(actual: exit, expected: CliExit.Cancelled);
    }
    [Fact]
    public void ABuildClaimWaitHonoursCallerCancellationAndReleasesItsWaiter() {
        using var scratch = new TemporaryDirectory();
        var store = new WorldArtifactStore(root: scratch.RootPath);
        const string Key = "cancelled-build";
        using var holder = store.AcquireBuild(key: Key, clock: Stopwatch.StartNew(), budget: TimeSpan.FromMinutes(minutes: 1), waited: out _, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(@object: holder);
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(testCode: () => store.AcquireBuild(
            key: Key, clock: Stopwatch.StartNew(), budget: TimeSpan.FromMinutes(minutes: 1), waited: out _,
            waiting: cancellation.Cancel, cancellationToken: cancellation.Token));
        holder.Dispose();
        using var next = store.AcquireBuild(key: Key, clock: Stopwatch.StartNew(), budget: TimeSpan.Zero, waited: out _, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(@object: next);
    }
}
