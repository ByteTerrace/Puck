using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Cli.Lanes;
using Xunit;

namespace Puck.Cli.Worktrees.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck lanes</c> reads each worktree's git state, newest write, log, lease and stall
/// state, matches leases to lanes by the checkout their holders and waiters recorded, prints changes between polls, and
/// leaves every file, index and lease as it found them.</summary>
public sealed class LanesCommandLawTests {
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => (DateTimeOffset.UtcNow + offset);
    }

    private static void Commit(string worktree, string name, string message) {
        File.WriteAllText(path: Path.Combine(path1: worktree, path2: name), contents: (name + "\n"));
        _ = CliGit.Run(worktree, "add", name);
        var result = CliGit.Run(worktree, "-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "--message", message);

        Assert.True(condition: (result.ExitCode == 0), userMessage: result.Stderr);
    }
    private static string[] Metadata(string holder, int pid, long ticks) => [holder, DateTimeOffset.UtcNow.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), pid.ToString(provider: CultureInfo.InvariantCulture), ticks.ToString(provider: CultureInfo.InvariantCulture)];
    private static Dictionary<string, (string Text, long Written)> Snapshot(string directory) => (Directory.Exists(path: directory)
        ? Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: "*").ToDictionary(keySelector: file => Path.GetRelativePath(path: file, relativeTo: directory), elementSelector: file => (((Path.GetExtension(path: file) == ".guard") ? string.Empty : File.ReadAllText(path: file)), File.GetLastWriteTimeUtc(path: file).Ticks), comparer: StringComparer.Ordinal)
        : []);

    private sealed class Layout : IDisposable {
        public GitScratchCheckout Checkout { get; } = new();
        public string Leases => Path.Combine(path1: Path.GetDirectoryName(path: Checkout.Root)!, path2: "leases");
        public Dictionary<string, string> Paths { get; } = [];

        public Layout() {
            Checkout.Write(name: "tracked.txt", text: "base\n");
            _ = Checkout.Commit(message: "base");
            foreach (var lane in new[] { "lane-a", "lane-b", "lane-c", "lane-d", "lane-e" }) {
                _ = Checkout.Git("branch", lane);
                Paths[lane] = Checkout.AddWorktree(name: lane, revision: lane);
            }
            // lane-a: holds the gpu lease, with one unstaged change. lane-b: waits for it. lane-c: finished by trailer.
            // lane-d: its log ends with an exit line. lane-e: no lease, no log, nothing to say it is alive.
            Commit(worktree: Paths["lane-a"], name: "a.txt", message: "lane-a: work");
            File.WriteAllText(path: Path.Combine(path1: Paths["lane-a"], path2: "tracked.txt"), contents: "changed\n");
            Commit(worktree: Paths["lane-c"], name: "c.txt", message: "lane-c: work\n\nBrief: velocity-c done");
            Commit(worktree: Paths["lane-d"], name: "d.txt", message: "lane-d: work");
            using var self = Process.GetCurrentProcess();
            var ticks = self.StartTime.ToUniversalTime().Ticks;

            // The holder and the live waiter are named for no lane; only the checkouts recorded beside them say whose
            // they are. The guard is what makes the holder live.
            _ = Directory.CreateDirectory(path: Path.Combine(path1: Leases, path2: "gpu"));
            Guard = FileLock.TryAcquire(path: Path.Combine(path1: Leases, path2: "gpu.guard"));
            File.WriteAllLines(path: Path.Combine(path1: Leases, path2: "gpu", path3: "holder"), contents: Metadata(holder: "boot-runner", pid: Environment.ProcessId, ticks: ticks));
            File.WriteAllText(path: Path.Combine(path1: Leases, path2: "gpu", path3: "checkout"), contents: (Paths["lane-a"] + "\n"));
            _ = Directory.CreateDirectory(path: Path.Combine(path1: Leases, path2: "gpu.queue"));
            File.WriteAllLines(path: Path.Combine(path1: Leases, path2: "gpu.queue", path3: "000000000003"), contents: Metadata(holder: "lane-e dead", pid: 2147483, ticks: 1));
            File.WriteAllLines(path: Path.Combine(path1: Leases, path2: "gpu.queue", path3: "000000000007"), contents: Metadata(holder: "capture-runner", pid: Environment.ProcessId, ticks: ticks));
            _ = Directory.CreateDirectory(path: Path.Combine(path1: Leases, path2: "gpu.checkouts"));
            File.WriteAllText(path: Path.Combine(path1: Leases, path2: "gpu.checkouts", path3: "000000000007"), contents: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Environment.ProcessId}\n{ticks}\n{Paths["lane-b"]}\n"));
            _ = Directory.CreateDirectory(path: Path.Combine(path1: Checkout.Root, path2: ".claude", path3: "scratch"));
        }

        public void ExitLog(string lane, string line) => File.WriteAllText(path: Path.Combine(path1: Checkout.Root, path2: ".claude", path3: "scratch", path4: (lane + ".log")), contents: ("working\n" + line + "\n"));
        public FileStream? Guard { get; set; }

        public Task<IReadOnlyList<LanesCommand.LaneRow>> SnapshotAsync(TimeSpan later) => LanesCommand.SnapshotAsync(baseRef: "main", leaseRoot: Leases, now: (DateTimeOffset.UtcNow + later), repository: Checkout.Root, stall: TimeSpan.FromMinutes(minutes: 30));
        public void Dispose() {
            Guard?.Dispose();
            Checkout.Dispose();
        }
    }

    [Fact]
    public async Task AHolderFileNoProcessHoldsTheGuardForHoldsNothing() {
        using var layout = new Layout();

        // The holder file still names this live process, but nothing holds the guard: the lock decides, not the pid.
        layout.Guard!.Dispose();
        layout.Guard = null;
        var rows = (await layout.SnapshotAsync(later: TimeSpan.Zero)).ToDictionary(keySelector: static row => row.Name);

        Assert.Empty(collection: rows["lane-a"].Leases);
        Assert.False(condition: CliLease.Inspect(name: "gpu", now: DateTimeOffset.UtcNow, root: layout.Leases).HolderAlive);
    }

    [Fact]
    public async Task EachWorktreeReportsItsGitStateLeaseLogAndStall() {
        using var layout = new Layout();

        layout.ExitLog(lane: "lane-d", line: "codex exit 3");
        var rows = (await layout.SnapshotAsync(later: TimeSpan.FromHours(hours: 2))).ToDictionary(keySelector: static row => row.Name);

        Assert.Equal(expected: 6, actual: rows.Count);
        Assert.True(condition: rows.Values.First().Main);
        Assert.Equal(expected: "main", actual: rows[Path.GetFileName(path: layout.Checkout.Root)].State);
        var a = rows["lane-a"];

        Assert.Equal(expected: 1, actual: a.Ahead);
        Assert.Equal(expected: 1, actual: a.Files);
        Assert.Equal(expected: 1, actual: a.Unstaged);
        Assert.Equal(expected: "lane-a: work", actual: a.Subject);
        Assert.Equal(expected: ["gpu:holding"], actual: a.Leases.Select(selector: static use => use.ToString()));
        Assert.False(condition: a.Stall);
        var b = Assert.Single(collection: rows["lane-b"].Leases);

        Assert.Equal(expected: ("queued", "7", 0), actual: (b.State, b.Ticket, b.Ahead));
        Assert.False(condition: rows["lane-b"].Stall);
        Assert.Equal(expected: "done", actual: rows["lane-c"].State);
        Assert.Equal(expected: "velocity-c done", actual: rows["lane-c"].Brief);
        Assert.False(condition: rows["lane-c"].Stall);
        Assert.Equal(expected: ("exited", "codex exit 3"), actual: (rows["lane-d"].State, rows["lane-d"].LogExit));
        Assert.False(condition: rows["lane-d"].Stall);
        Assert.Equal(expected: ("active", true), actual: (rows["lane-e"].State, rows["lane-e"].Stall));
        Assert.Empty(collection: rows["lane-e"].Leases);
        Assert.Contains(expectedSubstring: "STALL", actualString: LanesCommand.Line(row: rows["lane-e"], now: DateTimeOffset.UtcNow.AddHours(hours: 2)));
        // The same layout read now has written within the interval, so nothing stalls.
        Assert.DoesNotContain(collection: await layout.SnapshotAsync(later: TimeSpan.Zero), filter: static row => row.Stall);
    }

    [Fact]
    public async Task TheReportChangesNothingAndTheJsonFormListsEveryLane() {
        using var layout = new Layout();
        var leases = Snapshot(directory: layout.Leases);
        var index = File.ReadAllBytes(path: Path.Combine(path1: layout.Checkout.Root, path2: ".git/worktrees/lane-a/index"));
        var indexWritten = File.GetLastWriteTimeUtc(path: Path.Combine(path1: layout.Checkout.Root, path2: ".git/worktrees/lane-a/index"));
        var result = await ConsoleCapture.RunSplitAsync(run: () => LanesCommand.ExecuteAsync(baseRef: "main", clock: TimeProvider.System, json: true, leaseRoot: layout.Leases, repository: layout.Paths["lane-a"], stall: TimeSpan.FromMinutes(minutes: 30)));

        Assert.True(condition: (result.ExitCode == 0), userMessage: result.Error);
        using var document = JsonDocument.Parse(json: result.Output);
        var lanes = document.RootElement.GetProperty(propertyName: "lanes").EnumerateArray().ToArray();

        Assert.Equal(expected: 6, actual: lanes.Length);
        Assert.Contains(collection: lanes, filter: static lane => (lane.GetProperty(propertyName: "row").GetProperty(propertyName: "name").GetString() == "lane-b"));
        Assert.Equal(expected: leases, actual: Snapshot(directory: layout.Leases));
        Assert.Equal(expected: index, actual: File.ReadAllBytes(path: Path.Combine(path1: layout.Checkout.Root, path2: ".git/worktrees/lane-a/index")));
        Assert.Equal(expected: indexWritten, actual: File.GetLastWriteTimeUtc(path: Path.Combine(path1: layout.Checkout.Root, path2: ".git/worktrees/lane-a/index")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: layout.Leases, path2: "gpu.queue", path3: "000000000003")), userMessage: "a dead ticket is left for its next waiter to drop");
        var text = await ConsoleCapture.RunSplitAsync(run: () => LanesCommand.ExecuteAsync(baseRef: "main", clock: new OffsetClock(offset: TimeSpan.FromHours(hours: 2)), json: false, leaseRoot: layout.Leases, repository: layout.Checkout.Root, stall: TimeSpan.FromMinutes(minutes: 30)));

        Assert.Equal(expected: 6, actual: text.Output.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries).Length);
        // Only the two active lanes with no lease and no log are stalled: the holder, the waiter, the finished lane and the main checkout are not.
        Assert.Equal(expected: ["lane-d", "lane-e"], actual: text.Output.Split(separator: '\n').Where(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "STALL")).Select(selector: static line => line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)[1]));
    }

    [Fact]
    public async Task AFindingsOnlyLaneIsJudgedByItsLogAndReportAndADeliveryOutranksTheExit() {
        using var layout = new Layout();
        var scratch = Path.Combine(path1: layout.Checkout.Root, path2: ".claude", path3: "scratch");
        var later = TimeSpan.FromHours(hours: 2);

        // lane-e declares itself findings-only: two hours without a write is no stall while its log is still empty.
        File.WriteAllText(path: Path.Combine(path1: scratch, path2: "lane-e.task"), contents: "mode: findings-only\n");
        var e = (await layout.SnapshotAsync(later: later)).Single(predicate: static row => (row.Name == "lane-e"));

        Assert.Equal(expected: ("findings-only", "active", false), actual: (e.Mode, e.State, e.Stall));
        // Once its log has output, two hours without more is a stall, reported as log silence.
        File.WriteAllText(path: Path.Combine(path1: scratch, path2: "lane-e.log"), contents: "working\n");
        var before = await layout.SnapshotAsync(later: later);

        Assert.True(condition: before.Single(predicate: static row => (row.Name == "lane-e")).Stall);
        // Its report arrives and then its process exits: the lane reported; it did not merely exit.
        File.WriteAllText(path: Path.Combine(path1: scratch, path2: "lane-e-findings.md"), contents: "findings\n");
        layout.ExitLog(lane: "lane-e", line: "agy exit 0");
        // lane-c committed its Brief trailer and then its process exited: done.
        layout.ExitLog(lane: "lane-c", line: "codex exit 0");
        // lane-d ran Codex in a read-only sandbox and exited without a report.
        File.WriteAllText(path: Path.Combine(path1: scratch, path2: "lane-d.log"), contents: "OpenAI Codex v0.164.0\n--------\nsandbox: read-only\n--------\nworking\ncodex exit 1\n");
        var after = await layout.SnapshotAsync(later: later);
        var rows = after.ToDictionary(keySelector: static row => row.Name);

        Assert.Equal(expected: ("reported", false), actual: (rows["lane-e"].State, rows["lane-e"].Stall));
        Assert.EndsWith(expectedEndString: "lane-e-findings.md", actualString: rows["lane-e"].Report);
        Assert.Equal(expected: "done", actual: rows["lane-c"].State);
        Assert.Equal(expected: ("findings-only", "exited", null), actual: (rows["lane-d"].Mode, rows["lane-d"].State, rows["lane-d"].Report));
        var messages = LanesCommand.Changes(previous: before, current: after, now: DateTimeOffset.UtcNow.Add(later));

        Assert.Contains(collection: messages, filter: static message => (message.StartsWith(comparisonType: StringComparison.Ordinal, value: "lane-e: finished (reported, agy exit 0") && message.Contains(value: "report ", comparisonType: StringComparison.Ordinal)));
        Assert.Contains(collection: messages, filter: static message => (message == "lane-d: finished (exited, codex exit 1; the process exited without a report)"));
    }

    [Fact]
    public async Task WatchingPrintsOnlyWhatChanged() {
        using var layout = new Layout();
        var before = await layout.SnapshotAsync(later: TimeSpan.FromHours(hours: 2));
        var now = DateTimeOffset.UtcNow.AddHours(hours: 2);

        Assert.Empty(collection: LanesCommand.Changes(previous: before, current: before, now: now));
        layout.ExitLog(lane: "lane-e", line: "agy exit 0");
        File.Delete(path: Path.Combine(path1: layout.Leases, path2: "gpu", path3: "holder"));
        var messages = LanesCommand.Changes(previous: before, current: await layout.SnapshotAsync(later: TimeSpan.FromHours(hours: 2)), now: now);

        Assert.Contains(collection: messages, filter: static message => message.StartsWith(comparisonType: StringComparison.Ordinal, value: "lane-e: finished (exited, agy exit 0"));
        Assert.Contains(collection: messages, filter: static message => (message == "lane-a: lease gpu:holding -> -"));
        // Without its lease the lane that held it now reads as stalled; the queued and the already-stalled lanes report nothing.
        Assert.Contains(collection: messages, filter: static message => message.StartsWith(comparisonType: StringComparison.Ordinal, value: "lane-a: STALL"));
        Assert.Equal(expected: 3, actual: messages.Count);
    }
}
