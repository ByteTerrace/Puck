using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HeavyLeaseTests {
    [Fact]
    public async Task HeavyAdmissionSerializesSiblingsEvenInsideAnOuterLease() {
        // Keep this test independent of other suites' real machine lease. The outer guard is
        // real; its metadata names our live parent to model an enclosing `puck lease` process.
        using var checkout = new GitScratchCheckout();
        var root = checkout.Root;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        cancellation.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 30));
        var leaseRoot = await CliLease.RootAsync(checkout: root, cancellationToken: cancellation.Token);
        using var outer = await CliLease.AcquireAsync(root: leaseRoot, name: "heavy", holder: "doctrine-outer", wait: TimeSpan.Zero, clock: TimeProvider.System, cancellationToken: cancellation.Token);
        var metadataPath = Path.Combine(path1: leaseRoot, path2: "heavy/holder");
        var metadata = File.ReadAllLines(path: metadataPath);
        using var parent = System.Diagnostics.Process.GetProcessById(processId: HostProbe.ParentId(processId: Environment.ProcessId));

        metadata[2] = parent.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        metadata[3] = parent.StartTime.ToUniversalTime().Ticks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(contents: metadata, path: metadataPath);
        using var first = HostAdmission.AcquireHeavy(repositoryRoot: root, step: "doctrine-first", clock: TimeProvider.System, cancellationToken: cancellation.Token);
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = Task.Run(function: () => {
            entered.SetResult();
            return HostAdmission.AcquireHeavy(repositoryRoot: root, step: "doctrine-second", clock: TimeProvider.System, cancellationToken: cancellation.Token);
        }, cancellationToken: cancellation.Token);

        await entered.Task.WaitAsync(cancellationToken: cancellation.Token);
        await Task.Delay(delay: TimeSpan.FromMilliseconds(milliseconds: 250), cancellationToken: cancellation.Token);
        Assert.False(condition: waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting;
    }
}
