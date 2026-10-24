using System.Diagnostics;
using System.Globalization;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class CliLeaseTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: ("agents-slim-lease-" + Guid.NewGuid().ToString(format: "N")));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALeaseExcludesAnotherHolderAndReleasesOnDispose() {
        using (var first = await Take()) {
            var lines = File.ReadAllLines(path: Path.Combine(path1: m_root, path2: "gpu/holder"));

            Assert.Equal(expected: "agents-slim", actual: lines[0]);
            Assert.Equal(expected: Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture), actual: lines[2]);
            await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take());
            using var heavy = await Take(name: "heavy");
        }
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root, path2: "gpu")));
        using var second = await Take();

        second.Dispose();
        second.Dispose();
    }
    [Fact]
    public async Task WaitingUsesTheCliClockAndCancellationDoesNotReleaseAnotherHolder() {
        var clock = new VirtualClock();
        using var first = await Take();
        var waiting = Take(wait: TimeSpan.FromMilliseconds(milliseconds: 50), clock: clock);

        await clock.ExpireAsync(dueTime: TimeSpan.FromMilliseconds(milliseconds: 50), pending: waiting, ct: Token);
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await waiting);
        using var cancel = new CancellationTokenSource();
        var cancelled = CliLease.AcquireAsync(root: m_root, name: "gpu", holder: "waiter", wait: TimeSpan.FromSeconds(seconds: 30), clock: clock, cancellationToken: cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await cancelled);
        Assert.True(condition: File.Exists(path: Path.Combine(path1: m_root, path2: "gpu/holder")));
        var released = Take(wait: TimeSpan.FromSeconds(seconds: 1), clock: clock);

        await clock.WhenArmedAsync(dueTime: TimeSpan.FromMilliseconds(milliseconds: 100), count: 1, ct: Token);
        first.Dispose();
        clock.Advance(by: TimeSpan.FromMilliseconds(milliseconds: 100));
        using var next = await released;
    }
    [Fact]
    public async Task DeadAndReusedHoldersAreReclaimedImmediatelyButLiveHoldersNeverExpire() {
        var clock = new VirtualClock();
        var directory = Path.Combine(path1: m_root, path2: "gpu");

        Directory.CreateDirectory(path: directory);
        var path = Path.Combine(path1: directory, path2: "holder");
        using var process = Process.GetCurrentProcess();

        string[] Metadata(DateTimeOffset time, int pid, long start) => ["abandoned", time.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), pid.ToString(provider: CultureInfo.InvariantCulture), start.ToString(provider: CultureInfo.InvariantCulture)];
        var old = (clock.GetUtcNow() - TimeSpan.FromHours(hours: 2));

        File.WriteAllLines(path: path, contents: Metadata(time: old, pid: process.Id, start: process.StartTime.ToUniversalTime().Ticks));
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(clock: clock));
        File.WriteAllLines(path: path, contents: Metadata(time: clock.GetUtcNow(), pid: int.MaxValue, start: 1));
        using (var recovered = await Take(clock: clock)) { Assert.Equal(expected: "agents-slim", actual: File.ReadAllLines(path: path)[0]); }
        Directory.CreateDirectory(path: directory);
        File.WriteAllLines(contents: ["unrecognized"], path: path);
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(clock: clock));
        File.Delete(path: path);
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(clock: clock));
        File.WriteAllLines(path: path, contents: Metadata(pid: int.MaxValue, start: 1, time: old));
        using (var recovered = await Take(clock: clock)) { Assert.Equal(expected: "agents-slim", actual: File.ReadAllLines(path: path)[0]); }
        Directory.CreateDirectory(path: directory);
        File.WriteAllLines(path: path, contents: Metadata(time: clock.GetUtcNow(), pid: process.Id, start: 1));
        using var reusedPid = await Take(clock: clock);
    }
    [Fact]
    public async Task InvalidAcquisitionNeverCreatesALease() {
        await Assert.ThrowsAsync<ArgumentException>(testCode: async () => await Take(name: "../outside"));
        await Assert.ThrowsAsync<ArgumentException>(testCode: async () => await Take(wait: TimeSpan.FromSeconds(seconds: -1)));
        await Assert.ThrowsAsync<ArgumentException>(testCode: async () => await CliLease.AcquireAsync(root: m_root, name: "gpu", holder: "", wait: TimeSpan.Zero, clock: TimeProvider.System, cancellationToken: Token));
        Assert.False(condition: Directory.Exists(path: m_root));
    }
    [Fact]
    public async Task TheCommandForwardsArgumentsAndFailureAndAlwaysReleases() {
        var command = CliRoot.Compose(description: "Lease test", verbs: [LeaseCommand.Create(clock: TimeProvider.System, leaseRoot: m_root)]);
        var args = (OperatingSystem.IsWindows() ? new[] { "cmd.exe", "/c", "exit 7" } : new[] { "/bin/sh", "-c", "exit 7" });

        Assert.Equal(expected: 7, actual: await CliRoot.InvokeAsync(args: ["lease", "heavy", "--holder", "agents-slim", "--", .. args], root: command));
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root, path2: "heavy")));
        Assert.Equal(expected: 2, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--wait", "-1", "--", "dotnet", "--version"], root: command));
        Assert.Equal(expected: 2, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--", "agents-slim-missing-executable"], root: command));
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root, path2: "gpu")));
    }
    [Fact]
    public async Task CancellingTheCommandKillsItsChildBeforeReleasingTheLease() {
        Directory.CreateDirectory(path: m_root);
        var ready = new TaskCompletionSource<int>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(filter: "child-*", path: m_root);

        watcher.Created += (_, change) => {
            if (int.TryParse(s: change.Name?[6..], result: out var pid)) { ready.TrySetResult(result: pid); }
        };
        watcher.EnableRaisingEvents = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var marker = Path.Combine(path1: m_root, path2: "child-");
        var args = (OperatingSystem.IsWindows()
            ? new[] { "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", (("[void][IO.Directory]::CreateDirectory('" + marker.Replace(newValue: "''", oldValue: "'")) + "' + $PID); Start-Sleep -Seconds 60") }
            : new[] { "/bin/sh", "-c", (("mkdir '" + marker.Replace(newValue: "'\\''", oldValue: "'")) + "'$$; sleep 60") });
        var command = CliRoot.Compose(description: "Lease test", verbs: [LeaseCommand.Create(clock: TimeProvider.System, leaseRoot: m_root)]);
        var parsed = command.Parse(args: ["lease", "gpu", "--holder", "agents-slim", "--", .. args]);
        var run = parsed.InvokeAsync(configuration: CliRoot.Invocation(), cancellationToken: cancellation.Token);
        int pid;

        try { pid = await ready.Task.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 15), cancellationToken: Token); } finally { await cancellation.CancelAsync(); }
        Assert.Equal(expected: 130, actual: await run);
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root, path2: "gpu")));
        try {
            using var child = Process.GetProcessById(processId: pid);

            Assert.True(condition: child.HasExited);
        } catch (ArgumentException) { }
    }

    private Task<CliLease> Take(string name = "gpu", TimeSpan wait = default, TimeProvider? clock = null) => CliLease.AcquireAsync(root: m_root, name: name, holder: "agents-slim", wait: wait, clock: (clock ?? TimeProvider.System), cancellationToken: Token);

    public void Dispose() {
        if (Directory.Exists(path: m_root)) { Directory.Delete(path: m_root, recursive: true); }
    }
}
