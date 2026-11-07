using System.Diagnostics;
using System.Globalization;
using Puck.Abstractions;
using Puck.Confinement;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class CliLeaseTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: ("agents-slim-lease-" + Guid.NewGuid().ToString(format: "N")));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InspectPrunesAnUnlockedQuietHolderButNeverAnActiveReservation() {
        using (var quiet = await CliLease.AcquireQuietAsync(m_root, "scheduler quiet", TimeSpan.Zero, TimeProvider.System, m_root, Token)) {
            Assert.True(CliLease.Inspect(m_root, "quiet", DateTimeOffset.UtcNow).HolderAlive);
            Assert.True(Directory.Exists(Path.Combine(m_root, "quiet")));
        }
        var directory = Path.Combine(m_root, "quiet");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "holder"), $"dead quiet\n{DateTimeOffset.UtcNow:O}\n2147483647\n1\n");
        Assert.Null(CliLease.Inspect(m_root, "quiet", DateTimeOffset.UtcNow).Holder);
        Assert.False(Directory.Exists(directory));
        Assert.True(File.Exists(directory + ".guard"));
    }

    [Fact]
    public async Task QuietPriorityLawDrainsTwoHoldersAndBlocksEveryCompetingGrant() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(100);
        using var heavy = await Take(name: "heavy", holder: "working-heavy");
        using var gpu = await Take(name: "gpu", holder: "working-gpu");
        var competitor = Take(name: "heavy", holder: "queued-first", wait: TimeSpan.FromMinutes(1), clock: clock);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        var window = CliLease.AcquireQuietAsync(m_root, "measurement", TimeSpan.FromMinutes(1), clock, m_root, Token);
        await clock.WhenArmedAsync(count: 2, ct: Token, dueTime: tick);
        await Assert.ThrowsAsync<TimeoutException>(async () => await Take(name: "build", holder: "new-build", clock: clock));
        heavy.Dispose();
        clock.Advance(tick);
        await clock.WhenArmedAsync(count: 2, ct: Token, dueTime: tick);
        Assert.False(competitor.IsCompleted);
        Assert.False(window.IsCompleted);
        gpu.Dispose();
        clock.Advance(tick);
        using var quiet = await window.WaitAsync(Token);
        Assert.False(competitor.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(async () => await Take(name: "gpu", clock: clock));
        using (var nested = await CliLease.AcquireAsync(m_root, "build", "nested", TimeSpan.Zero, clock, m_root, Token, ancestor: (pid, _) => pid == Environment.ProcessId)) {
            Assert.True(File.Exists(Path.Combine(m_root, "build.child/holder")));
        }
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        quiet.Dispose();
        clock.Advance(tick);
        using var resumed = await competitor.WaitAsync(Token);
    }

    // Law (the grant door's invariant): a current holder's process tree always finishes. Lane A holds gpu and its canary nests
    // a gpu acquisition; a measurement window holds heavy and build and waits for gpu. The nested acquisition is granted at
    // once beside its parent's lease, and the window takes gpu when lane A releases it.
    [Fact]
    public async Task ANestedAcquisitionUnderAHolderProceedsWhileQuietWaitsAndQuietThenGetsTheLease() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(100);
        var laneA = await Take(name: "gpu", holder: "lane-a run");
        var window = CliLease.AcquireQuietAsync(m_root, "measurement", TimeSpan.FromMinutes(1), clock, m_root, Token);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        Assert.False(window.IsCompleted);
        using (var canary = await CliLease.AcquireAsync(m_root, "gpu", "lane-a canary", TimeSpan.Zero, clock, m_root, Token, ancestor: Self)) {
            Assert.Equal("lane-a canary", File.ReadAllLines(Path.Combine(m_root, "gpu.child/holder"))[0]);
            Assert.False(window.IsCompleted);
        }
        laneA.Dispose();
        clock.Advance(tick);
        using var quiet = await window.WaitAsync(Token);
        Assert.Equal("measurement", File.ReadAllLines(Path.Combine(m_root, "gpu/holder"))[0]);
    }

    // Law: holder metadata that names an ancestor proves nothing without its guard, so an acquisition beside a stale holder
    // file takes the lease itself instead of a child lease no one guards.
    [Fact]
    public async Task HolderMetadataNamingAnAncestorWithoutItsGuardIsNoProofToNest() {
        using var process = Process.GetCurrentProcess();
        Directory.CreateDirectory(Path.Combine(m_root, "gpu"));
        File.WriteAllLines(Path.Combine(m_root, "gpu/holder"), ["gone parent", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), process.Id.ToString(CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)]);
        using var lease = await CliLease.AcquireAsync(m_root, "gpu", "orphan", TimeSpan.Zero, TimeProvider.System, m_root, Token, ancestor: Self);
        Assert.Equal("orphan", File.ReadAllLines(Path.Combine(m_root, "gpu/holder"))[0]);
        Assert.False(Directory.Exists(Path.Combine(m_root, "gpu.child")));
    }

    // Law: the route is decided again on every pass. A descendant waiting on its parent's child lease, whose parent then
    // releases to another lane, queues for the lease itself: it never takes the child lease while the other lane holds gpu,
    // and the parent's release cannot delete the child state beside it.
    [Fact]
    public async Task ANestedWaiterWhoseParentReleasesQueuesForTheLeaseItselfAndNeverTakesTheChild() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(100);
        var parent = await Take(name: "gpu", holder: "lane-a run");
        var sibling = await CliLease.AcquireAsync(m_root, "gpu", "lane-a sibling", TimeSpan.Zero, clock, m_root, Token, ancestor: Self);
        // The sibling is another process under the parent, never the canary's ancestor.
        Impersonate(directory: "gpu.child", holder: "lane-a sibling");
        var waiting = CliLease.AcquireAsync(m_root, "gpu", "lane-a canary", TimeSpan.FromMinutes(10), clock, m_root, Token, ancestor: Self);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        parent.Dispose();
        Assert.True(File.Exists(Path.Combine(m_root, "gpu.child/holder")));
        using var other = await Take(name: "gpu", holder: "lane-b run");
        // Lane B is another process: its holder identity is no ancestor of lane A's canary.
        Impersonate(directory: "gpu", holder: "lane-b run");
        clock.Advance(tick);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        sibling.Dispose();
        clock.Advance(tick);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        Assert.False(waiting.IsCompleted);
        Assert.False(Directory.Exists(Path.Combine(m_root, "gpu.child")));
        other.Dispose();
        clock.Advance(tick);
        using var granted = await waiting.WaitAsync(Token);
        Assert.Equal("lane-a canary", File.ReadAllLines(Path.Combine(m_root, "gpu/holder"))[0]);
    }

    // Law: an acquisition is decided on the lease, not on the admission lock another waiter holds for its own pass, so
    // `--wait 0` on an idle lease is granted once that pass ends.
    [Fact]
    public async Task AZeroWaitOnAnIdleLeaseIsGrantedPastABusyAdmissionLock() {
        Directory.CreateDirectory(m_root);
        var admission = FileLock.TryAcquire(Path.Combine(m_root, "admission.guard"));
        Assert.NotNull(admission);
        var taking = Take(name: "gpu");
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.False(taking.IsCompleted);
        admission.Dispose();
        using var lease = await taking.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    // Law: a CLI that speaks a lower lease protocol than the lease directory records refuses by name, naming both protocols
    // and the rebuild, instead of queuing beside CLIs that read the files differently; `lease list` names the mismatch.
    [Fact]
    public async Task ALowerProtocolCliIsRefusedByNameAndQueuesNothing() {
        Directory.CreateDirectory(m_root);
        File.WriteAllText(Path.Combine(m_root, "protocol"), (CliLease.Protocol + 1).ToString(CultureInfo.InvariantCulture) + "\n");
        var refused = await Assert.ThrowsAsync<LeaseProtocolException>(async () => await Take(wait: TimeSpan.FromMinutes(1)));
        Assert.Equal((CliLease.Protocol + 1, CliLease.Protocol), (refused.Recorded, refused.Spoken));
        Assert.Contains($"lease protocol {CliLease.Protocol + 1}", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"lease protocol {CliLease.Protocol}:", refused.Message, StringComparison.Ordinal);
        Assert.Contains("rebuild the CLI", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(m_root, "gpu.queue")));
        Assert.Equal(CliLease.Protocol + 1, CliLease.RecordedProtocol(m_root));
        Assert.StartsWith("protocol: ", LeaseCommand.Render(TimeProvider.System, m_root), StringComparison.Ordinal);
    }

    // Law: the newest CLI to queue raises the stamp under the admission lock, and a waiter already queued from an older CLI
    // leaves the queue by name on its next pass instead of waiting beside it.
    [Fact]
    public async Task AHigherProtocolRaisesTheStampAndAQueuedLowerWaiterLeavesByName() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(100);
        Directory.CreateDirectory(m_root);
        File.WriteAllText(Path.Combine(m_root, "protocol"), "0\n");
        using var holder = await Take(holder: "holder");
        Assert.Equal(CliLease.Protocol, CliLease.RecordedProtocol(m_root));
        var waiting = Take(wait: TimeSpan.FromMinutes(10), clock: clock, holder: "waiting");
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        File.WriteAllText(Path.Combine(m_root, "protocol"), (CliLease.Protocol + 1).ToString(CultureInfo.InvariantCulture) + "\n");
        clock.Advance(tick);
        await Assert.ThrowsAsync<LeaseProtocolException>(async () => await waiting.WaitAsync(Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(m_root, "gpu.queue")));
    }

    [Fact]
    public async Task CancelledQuietReservationReleasesPriorityAndPartialHoldings() {
        var clock = new VirtualClock();
        using var gpu = await Take(name: "gpu");
        using var cancel = new CancellationTokenSource();
        var window = CliLease.AcquireQuietAsync(m_root, "measurement", TimeSpan.FromMinutes(1), clock, m_root, cancel.Token);
        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: TimeSpan.FromMilliseconds(100));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await window);
        using var build = await Take(name: "build");
        using var heavy = await Take(name: "heavy");
        Assert.Empty(CliLease.Inspect(m_root, "quiet", clock.GetUtcNow()).Queue);
    }

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
        var cancelled = CliLease.AcquireAsync(checkout: m_root, root: m_root, name: "gpu", holder: "waiter", wait: TimeSpan.FromSeconds(seconds: 30), clock: clock, cancellationToken: cancel.Token);

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
            waiting.Add(item: (name, CliLease.AcquireAsync(checkout: m_root, root: m_root, name: "gpu", holder: name, lane: lane, wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, cancellationToken: Token)));
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
    public async Task AHolderAndAWaiterRecordTheirCheckoutsAndTheGuardDecidesLiveness() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);
        var holderCheckout = Path.Combine(path1: m_root, path2: "checkouts", path3: "holding");
        var waiterCheckout = Path.Combine(path1: m_root, path2: "checkouts", path3: "waiting");
        var holder = await CliLease.AcquireAsync(checkout: holderCheckout, clock: clock, holder: "boot-runner", name: "gpu", root: m_root, wait: TimeSpan.Zero, cancellationToken: Token);
        var waiting = CliLease.AcquireAsync(checkout: waiterCheckout, clock: clock, holder: "capture-runner", name: "gpu", root: m_root, wait: TimeSpan.FromMinutes(minutes: 10), cancellationToken: Token);

        await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: tick);
        var state = CliLease.Inspect(name: "gpu", now: clock.GetUtcNow(), root: m_root);

        Assert.True(condition: state.HolderAlive);
        Assert.True(condition: CliPaths.Same(left: state.Checkout!, right: holderCheckout));
        Assert.True(condition: CliPaths.Same(left: Assert.Single(collection: state.Queue).Checkout!, right: waiterCheckout));
        // Five metadata lines at most, the shape every CLI on the machine reads; the checkout lives beside them.
        Assert.InRange(actual: File.ReadAllLines(path: Path.Combine(path1: m_root, path2: "gpu/holder")).Length, high: 5, low: 4);

        holder.Dispose();
        clock.Advance(by: tick);
        using (await waiting) {
            Assert.True(condition: CliPaths.Same(left: CliLease.Inspect(name: "gpu", now: clock.GetUtcNow(), root: m_root).Checkout!, right: waiterCheckout));
        }
        Assert.Empty(collection: Directory.EnumerateFiles(path: Path.Combine(path1: m_root, path2: "gpu.checkouts")));
    }
    [Fact]
    public async Task ALaneThatNeverHeldTheLeaseDoesNotOvertakeALaneThatWaitedBeforeIt() {
        var clock = new VirtualClock();
        var tick = TimeSpan.FromMilliseconds(milliseconds: 100);

        (await Take(holder: "veteran probe")).Dispose();
        var holder = await Take(holder: "holder");
        var waiting = new List<(string Holder, Task<CliLease> Task)>();

        async Task Queue(string name) {
            waiting.Add(item: (name, CliLease.AcquireAsync(checkout: m_root, root: m_root, name: "gpu", holder: name, wait: TimeSpan.FromMinutes(minutes: 10), clock: clock, cancellationToken: Token)));
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
        await Assert.ThrowsAsync<ArgumentException>(testCode: async () => await CliLease.AcquireAsync(checkout: m_root, root: m_root, name: "gpu", holder: "", wait: TimeSpan.Zero, clock: TimeProvider.System, cancellationToken: Token));
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
        Assert.Equal(expected: 0, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--kind", "Step", "--", .. args], root: command));
        Assert.Equal(expected: 0, actual: await CliRoot.InvokeAsync(args: ["lease", "build", "--holder", "agents-slim", "--", .. args], root: command));
        Assert.Equal(expected: 3, actual: asked.Count);
        Assert.Equal(expected: ("lease heavy agents-slim", false, true, true), actual: (asked[0].Step, asked[0].Device, asked[0].Heavy, asked[0].Held));
        Assert.Equal(expected: [HostStartKind.HeavySuite, HostStartKind.WindowedWorld, HostStartKind.Step], actual: [asked[0].Kind, asked[1].Kind, asked[2].Kind]);
        Assert.InRange(actual: asked[0].Bound!.Value, low: TimeSpan.FromSeconds(seconds: 50), high: TimeSpan.FromSeconds(seconds: 60));
        Assert.Equal(expected: ("lease gpu agents-slim", true, false, true, TimeSpan.Zero), actual: (asked[1].Step, asked[1].Device, asked[1].Heavy, asked[1].Held, asked[1].Bound!.Value));
        Assert.Equal(expected: (true, false, true), actual: (asked[2].Device, asked[2].Heavy, asked[2].Held));

        refuse = true;
        var marker = Path.Combine(path1: m_root, path2: "ran");
        var touch = (OperatingSystem.IsWindows() ? new[] { "cmd.exe", "/c", "mkdir", marker } : new[] { "/bin/mkdir", marker });

        Assert.Equal(expected: 2, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--kind", "Step", "--", .. touch], root: command));
        Assert.False(condition: Directory.Exists(path: marker));
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root, path2: "gpu")));
        var admissions = asked.Count;
        Assert.Equal(expected: 2, actual: await CliRoot.InvokeAsync(args: ["lease", "gpu", "--holder", "agents-slim", "--kind", "99", "--", .. touch], root: command));
        Assert.Equal(expected: admissions, actual: asked.Count);
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

    // This process, recognized by its id and start ticks, as an acquisition nested under a lease this process holds sees its
    // holder; a holder file naming any other start ticks is another process.
    private static bool Self(int pid, long ticks) {
        using var process = Process.GetCurrentProcess();

        return ((pid == process.Id) && (ticks == process.StartTime.ToUniversalTime().Ticks));
    }

    // Rewrites a lease this process holds so its holder file names another process: this process's id with other start ticks.
    private void Impersonate(string directory, string holder) {
        using var process = Process.GetCurrentProcess();

        File.WriteAllLines(path: Path.Combine(path1: m_root, path2: directory, path3: "holder"), contents: [holder, DateTimeOffset.UtcNow.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), process.Id.ToString(provider: CultureInfo.InvariantCulture), (process.StartTime.ToUniversalTime().Ticks + 1).ToString(provider: CultureInfo.InvariantCulture)]);
    }

    private Task<CliLease> Take(string name = "gpu", TimeSpan wait = default, TimeProvider? clock = null, string holder = "agents-slim") => CliLease.AcquireAsync(checkout: m_root, root: m_root, name: name, holder: holder, wait: wait, clock: (clock ?? TimeProvider.System), cancellationToken: Token);

    public void Dispose() {
        ConfinedDirectory.RemoveTree(path: m_root);
    }
}
