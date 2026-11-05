using System.Diagnostics;
using System.Globalization;
using Puck.Confinement;
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
    [InlineData("live")]
    [InlineData("dead")]
    [InlineData("torn")]
    [InlineData("missing")]
    [InlineData("reused")]
    [Theory]
    public async Task HolderMetadataWithoutTheExclusiveGuardNeverBlocksAcquisition(string state) {
        var clock = new VirtualClock();
        var directory = Path.Combine(path1: m_root, path2: "gpu");

        Directory.CreateDirectory(path: directory);
        var path = Path.Combine(path1: directory, path2: "holder");
        using var process = Process.GetCurrentProcess();

        string[] Metadata(DateTimeOffset time, int pid, long start) => ["abandoned", time.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), pid.ToString(provider: CultureInfo.InvariantCulture), start.ToString(provider: CultureInfo.InvariantCulture), "abandoned"];
        var old = (clock.GetUtcNow() - TimeSpan.FromHours(hours: 2));

        if (state != "missing") {
            File.WriteAllLines(path: path, contents: state switch {
                "live" => Metadata(time: old, pid: process.Id, start: process.StartTime.ToUniversalTime().Ticks),
                "dead" => Metadata(pid: int.MaxValue, start: 1, time: old),
                "reused" => Metadata(time: old, pid: process.Id, start: 1),
                _ => ["unrecognized"],
            });
        }
        using var recovered = await Take(clock: clock);

        Assert.Equal(expected: "agents-slim", actual: File.ReadAllLines(path: path)[0]);
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(clock: clock));
    }
    [Fact]
    public async Task AReaderThatDelaysMetadataDeletionCannotKeepTheReleasedGuard() {
        var first = await Take();
        using var reader = new FileStream(path: Path.Combine(path1: m_root, path2: "gpu/holder"), mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite);

        first.Dispose();
        using var second = await Take();
    }
    [Fact]
    public async Task WaitersAreAdmittedInArrivalOrderAndAReacquiringHolderQueuesBehindThem() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);
        var holder = await Take(holder: "holder");
        var first = Take(wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, holder: "first");

        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        var second = Take(wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, holder: "second");

        await clock.WhenArmedAsync(count: 2, ct: Token, dueTime: tick);
        holder.Dispose();
        // Nothing has waited as long as `first`, so a holder that comes straight back is refused rather than admitted.
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(clock: clock, holder: "holder"));
        clock.Advance(by: tick);
        using var admitted = await first.WaitAsync(cancellationToken: Token);

        Assert.Equal(expected: "first", actual: File.ReadAllLines(path: Path.Combine(path1: m_root, path2: "gpu/holder"))[0]);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        Assert.False(condition: second.IsCompleted);
        admitted.Dispose();
        clock.Advance(by: tick);
        using var last = await second.WaitAsync(cancellationToken: Token);

        Assert.Equal(expected: "second", actual: File.ReadAllLines(path: Path.Combine(path1: m_root, path2: "gpu/holder"))[0]);
    }
    [Fact]
    public async Task AdmissionRotatesBetweenLanesAndStaysFirstComeWithinOne() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);
        var holder = await Take(holder: "x");
        var waiting = new List<(string Holder, Task<CliLease> Task)>();

        async Task Queue(string name, string? lane = null) {
            waiting.Add(item: (name, CliLease.AcquireAsync(root: m_root, name: "gpu", holder: name, lane: lane, wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, cancellationToken: Token)));
            await clock.WhenArmedAsync(count: waiting.Count, ct: Token, dueTime: tick);
        }

        // Lane a queues three runs before b and c queue one each; the lane is the holder's first word or the explicit lane.
        await Queue("a one");
        await Queue("a two");
        await Queue("a three");
        await Queue("b one");
        await Queue("renamed c", lane: "c");
        var order = new List<string>();

        holder.Dispose();
        while (waiting.Count > 0) {
            clock.Advance(by: tick);
            var admitted = await Task.WhenAny(tasks: waiting.Select(selector: entry => entry.Task)).WaitAsync(cancellationToken: Token);
            var entry = waiting.Single(predicate: entry => (entry.Task == admitted));

            waiting.Remove(item: entry);
            order.Add(item: entry.Holder);
            // The next waiter's timer arms again once it has re-checked its place.
            using (await admitted) { await clock.WhenArmedAsync(count: waiting.Count, ct: Token, dueTime: tick); }
        }
        Assert.Equal(actual: order, expected: new[] { "a one", "b one", "renamed c", "a two", "a three" });
    }
    // A lane that has held the lease before ranks by when it was last served; a lane that never has must not rank first
    // for that reason alone, or every new lane (one per worktree) would overtake a lane already waiting, however long.
    [Fact]
    public async Task ALaneThatNeverHeldTheLeaseDoesNotOvertakeALaneThatWaitedBeforeIt() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);

        (await Take(holder: "veteran probe")).Dispose();
        var holder = await Take(holder: "holder");
        var waiting = new List<(string Holder, Task<CliLease> Task)>();

        async Task Queue(string name) {
            waiting.Add(item: (name, CliLease.AcquireAsync(root: m_root, name: "gpu", holder: name, wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, cancellationToken: Token)));
            await clock.WhenArmedAsync(count: waiting.Count, ct: Token, dueTime: tick);
        }

        await Queue(name: "veteran waiting");
        await Queue(name: "fresh one");
        await Queue(name: "fresh two");
        var order = new List<string>();

        holder.Dispose();
        while (waiting.Count > 0) {
            clock.Advance(by: tick);
            var admitted = await Task.WhenAny(tasks: waiting.Select(selector: entry => entry.Task)).WaitAsync(cancellationToken: Token);
            var entry = waiting.Single(predicate: entry => (entry.Task == admitted));

            waiting.Remove(item: entry);
            order.Add(item: entry.Holder);
            using (await admitted) { await clock.WhenArmedAsync(count: waiting.Count, ct: Token, dueTime: tick); }
        }
        Assert.Equal(actual: order, expected: new[] { "veteran waiting", "fresh one", "fresh two" });
    }
    [Fact]
    public async Task AFourLineTicketRotatesUnderItsHoldersFirstWord() {
        var queue = Path.Combine(path1: m_root, path2: "gpu.queue");
        using var process = Process.GetCurrentProcess();

        // Lane b was served before lane a, which is the lane served last, and a's two older tickets carry no lane line.
        (await Take(holder: "b probe")).Dispose();
        (await Take(holder: "a probe")).Dispose();
        foreach (var (sequence, holder) in new[] { ("000000000002", "a one"), ("000000000003", "a two") }) {
            File.WriteAllLines(path: Path.Combine(path1: queue, path2: sequence), contents: [holder, DateTimeOffset.UtcNow.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), process.Id.ToString(provider: CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(provider: CultureInfo.InvariantCulture)]);
        }
        // Read as lane a, they rank behind lane b despite being older; read as lanes of their own (never served), they would not.
        using var other = await Take(holder: "b one");

        Assert.Equal(expected: 4, actual: File.ReadAllLines(path: Path.Combine(path1: m_root, path2: "gpu/holder")).Length);
        other.Dispose();
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await Take(holder: "a three"));
    }
    [Fact]
    public async Task ADeadWaitersTicketIsSkippedAndDropped() {
        var clock = new VirtualClock();
        var queue = Path.Combine(path1: m_root, path2: "gpu.queue");
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);

        Directory.CreateDirectory(path: queue);
        var dead = Path.Combine(path1: queue, path2: "000000000001");

        // A power trip leaves the ticket of a process that no longer exists, older than every live waiter.
        File.WriteAllLines(path: dead, contents: ["dead", clock.GetUtcNow().ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), int.MaxValue.ToString(provider: CultureInfo.InvariantCulture), "1", "dead"]);
        File.WriteAllLines(path: Path.Combine(path1: queue, path2: "000000000002"), contents: ["torn"]);
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: DateTime.UtcNow.AddMinutes(value: -5), path: Path.Combine(path1: queue, path2: "000000000002"));
        using var holder = await Take(holder: "holder");
        var waiting = Take(wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, holder: "waiting");

        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        holder.Dispose();
        clock.Advance(by: tick);
        using var admitted = await waiting.WaitAsync(cancellationToken: Token);

        Assert.False(condition: File.Exists(path: dead));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: queue, path2: "000000000002")));
    }
    [Fact]
    public async Task ATimedOutWaiterLeavesTheQueue() {
        var clock = new VirtualClock();
        var queue = Path.Combine(path1: m_root, path2: "gpu.queue");
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);
        var holder = await Take(holder: "holder");
        var impatient = Take(wait: (2 * tick), clock: clock, holder: "impatient");
        var patient = Take(wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, holder: "patient");

        await clock.WhenArmedAsync(count: 2, ct: Token, dueTime: tick);
        Assert.Equal(expected: 2, actual: Directory.GetFiles(path: queue).Length);
        clock.Advance(by: tick);
        await clock.WhenArmedAsync(count: 2, ct: Token, dueTime: tick);
        clock.Advance(by: tick);
        await Assert.ThrowsAsync<TimeoutException>(testCode: async () => await impatient.WaitAsync(cancellationToken: Token));
        Assert.Single(collection: Directory.GetFiles(path: queue));
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        holder.Dispose();
        clock.Advance(by: tick);
        using var admitted = await patient.WaitAsync(cancellationToken: Token);

        Assert.Empty(collection: Directory.GetFiles(path: queue));
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
    // Law: puck lease heavy and gpu wait on the host admission once they hold their lease, as a heavy suite and a device
    // step respectively, bounded by what --wait left; a refusal starts nothing and releases the lease. The build lease and
    // the quiet window (which settles on the same readings) take no admission of their own.
    [Fact]
    public async Task HeavyAndGpuLeasesAdmitThroughTheHostAdmissionHoldingTheLeaseAndARefusalStartsNothing() {
        var asked = new List<(string Step, bool Device, bool Heavy, bool Held, TimeSpan? Bound, HostStartKind Kind)>();
        var refuse = false;
        HostAdmit admit = (_, step, device, heavySuite, kind, bound, _, _, _) => {
            asked.Add(item: (step, device, heavySuite, Directory.Exists(path: Path.Combine(path1: m_root, path2: (device ? "gpu" : "heavy"))), bound, kind));

            return Task.FromResult(result: !refuse);
        };
        var command = CliRoot.Compose(description: "Lease test", verbs: [LeaseCommand.Create(admit: admit, clock: TimeProvider.System, leaseRoot: m_root)]);
        var args = (OperatingSystem.IsWindows() ? new[] { "cmd.exe", "/c", "exit 0" } : new[] { "/bin/sh", "-c", "exit 0" });

        Assert.Equal(expected: 0, actual: await CliRoot.InvokeAsync(args: ["lease", "heavy", "--holder", "agents-slim", "--wait", "60", "--", .. args], root: command));
        Assert.Equal(expected: 0, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--", .. args], root: command));
        Assert.Equal(expected: 0, actual: await CliRoot.InvokeAsync(args: ["lease", "build", "--holder", "agents-slim", "--", .. args], root: command));
        Assert.Equal(expected: 2, actual: asked.Count);
        Assert.Equal(expected: ("lease heavy agents-slim", false, true, true), actual: (asked[0].Step, asked[0].Device, asked[0].Heavy, asked[0].Held));
        Assert.Equal(expected: [HostStartKind.HeavySuite, HostStartKind.WindowedWorld], actual: [asked[0].Kind, asked[1].Kind]);
        Assert.InRange(actual: asked[0].Bound!.Value, low: TimeSpan.FromSeconds(seconds: 50), high: TimeSpan.FromSeconds(seconds: 60));
        Assert.Equal(expected: ("lease gpu agents-slim", true, false, true, TimeSpan.Zero), actual: (asked[1].Step, asked[1].Device, asked[1].Heavy, asked[1].Held, asked[1].Bound!.Value));

        refuse = true;
        var marker = Path.Combine(path1: m_root, path2: "ran");
        var touch = (OperatingSystem.IsWindows() ? new[] { "cmd.exe", "/c", "mkdir", marker } : new[] { "/bin/mkdir", marker });

        Assert.Equal(expected: 2, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--", .. touch], root: command));
        Assert.False(condition: Directory.Exists(path: marker));
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

    private Task<CliLease> Take(string name = "gpu", TimeSpan wait = default, TimeProvider? clock = null, string holder = "agents-slim") => CliLease.AcquireAsync(root: m_root, name: name, holder: holder, wait: wait, clock: (clock ?? TimeProvider.System), cancellationToken: Token);

    public void Dispose() {
        ConfinedDirectory.RemoveTree(path: m_root);
    }
}
