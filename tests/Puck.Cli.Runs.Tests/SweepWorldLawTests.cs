using Puck.Cli.Sweep;
using Puck.Cli.Motion;
using Puck.Confinement;
using System.Text.Json;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck sweep</c> drives a real headless debug World through <c>SweepWorld</c>, <c>SweepRun</c>
/// and <c>SweepCommand</c>, and silence is never success: a rule whose metric the answer lacks, a World the sweep can no
/// longer reach, a spent budget and a baseline folder used as the output each fail the run instead of reading clean, and a
/// World that never attaches releases the lease it was handed.
/// </summary>
public sealed class SweepWorldLawTests : IDisposable {
    private const string ShortPlan = """
        {
          "tools": [
            { "name": "settle-in", "lines": [], "until": { "line": "world.state fuzzer", "metric": "enabled", "in": ["0"] }, "everyTicks": 30, "maxTicks": 60 }
          ],
          "signals": [
            { "name": "watchdog", "line": "world.state watchdog", "parse": "cells", "recheck": true, "red": ["count > 0", "checked < 1"] },
            { "name": "frames", "line": "host.frames 5", "parse": "frames", "leg": "headless" },
            { "name": "memory", "line": "host.memory", "parse": "pairs" }
          ],
          "tour": {
            "settle": 5,
            "stops": [ { "name": "pinned", "group": "g", "lines": ["body.stop 0", "world.wait 70"], "steps": [ { "at": 2, "run": "body.stop 0" } ], "settle": 5 } ]
          }
        }
        """;

    private readonly string m_directory = Directory.CreateTempSubdirectory(prefix: "puck-sweep-world-law-").FullName;
    private readonly WorldArtifact? m_build;
    private readonly string m_entry;
    private readonly string m_root;

    public SweepWorldLawTests() {
        m_root = AppContext.BaseDirectory;

        while (!File.Exists(path: Path.Combine(path1: m_root, path2: "Puck.slnx"))) {
            m_root = Path.GetDirectoryName(path: m_root)!;
        }

        Assert.True(condition: WorldArtifactBuild.TryResolveNamed(error: out var error, lease: out m_build, logDirectory: m_directory, named: null, path: out var entry, repositoryRoot: m_root, timeout: TimeSpan.FromMinutes(value: 30), verb: "sweep-test"), userMessage: error);
        m_entry = entry!;
    }

    public void Dispose() {
        m_build?.Dispose();
        ConfinedDirectory.RemoveTree(path: m_directory);
    }

    private sealed class Lease : IDisposable {
        public bool Released { get; private set; }

        public void Dispose() => Released = true;
    }

    private string[] Arguments(string state) => [
        "--world", Path.Combine(path1: m_root, path2: SweepCommand.DefaultWorld),
        .. WorldDevices.NoneArguments,
        "--state-dir", Path.Combine(path1: m_directory, path2: state),
        "--exit-after-seconds", "300",
        "--headless", "true",
        "--unpaced", "true",
        "--paused",
    ];
    private SweepWorld Boot(string name, SweepBudget budget) {
        Assert.True(condition: SweepWorld.TryBoot(arguments: Arguments(state: name), artifact: m_entry, bootTimeout: TimeSpan.FromMinutes(value: 5), budget: budget, error: out var error, lease: null, logDirectory: Path.Combine(path1: m_directory, path2: name), session: out var world, workingDirectory: m_root), userMessage: error);

        return world!;
    }
    private static SweepPlan Plan(string json) {
        Assert.True(condition: SweepPlan.TryParse(error: out var error, json: json, plan: out var plan), userMessage: error);

        return plan;
    }
    private (int Exit, string Output, string Report) Sweep(string plan, string output, params string[] extra) {
        var file = Path.Combine(path1: m_directory, path2: $"{Path.GetFileName(path: output)}-plan.json");

        File.WriteAllText(contents: plan, path: file);

        var (exit, text) = ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: ["sweep", "--plan", file, "--output", output, .. extra]));

        return (exit, text, (File.Exists(path: Path.Combine(path1: output, path2: SweepReport.FileName)) ? File.ReadAllText(path: Path.Combine(path1: output, path2: SweepReport.FileName)) : string.Empty));
    }

    [Fact]
    public void AHeadlessSweepRunsItsPlanOnASteppedWorldAndFailsOnAMissingMetric() {
        var first = Path.Combine(path1: m_directory, path2: "first");

        var (exit, text, report) = Sweep(plan: ShortPlan, output: first);

        Assert.True(condition: (exit == 0), userMessage: text);
        Assert.Contains(actualString: report, expectedSubstring: "\"name\": \"watchdog\"");
        Assert.DoesNotContain(actualString: report, expectedSubstring: "windowed");

        // A rule that reads a metric the answer lacks is unmeasured, and an unmeasured signal fails the run.
        var (missing, missingText, missingReport) = Sweep(plan: ShortPlan.Replace(newValue: "\"nonexistent > 0\"", oldValue: "\"count > 0\""), output: Path.Combine(path1: m_directory, path2: "missing"), "--baseline", Path.Combine(path1: first, path2: SweepReport.FileName));

        Assert.Equal(actual: missing, expected: 1);
        Assert.Contains(actualString: missingText, expectedSubstring: "unmeasured");
        Assert.Contains(actualString: missingReport, expectedSubstring: "nonexistent");

        // A baseline folder named as the output would be overwritten before it is compared.
        var (refused, refusedText, _) = Sweep(plan: ShortPlan, output: first, "--baseline", Path.Combine(path1: first, path2: SweepReport.FileName));

        Assert.Equal(actual: refused, expected: 2);
        Assert.Contains(actualString: refusedText, expectedSubstring: "overwrite");
    }
    [Fact]
    public void ASweepWithoutTimeLeftReportsEverythingUnmeasuredAndStopsTheWorld() {
        using var cancel = new CancellationTokenSource();
        using var budget = new SweepBudget(caller: cancel.Token, total: TimeSpan.FromMinutes(value: 10));
        var plan = Plan(json: ShortPlan);
        int processId;

        using (var world = Boot(budget: budget, name: "spent")) {
            processId = world.ProcessId;

            var run = new SweepRun(baseline: null, directory: Path.Combine(path1: m_directory, path2: "spent-report"), plan: plan, progress: TextWriter.Null, world: world);

            Assert.Equal(expected: SweepLegs.Headless, actual: world.Leg);
            Assert.Equal(expected: SweepStatus.Clean, actual: Assert.Single(collection: run.Tools()).Status);
            Assert.All(collection: run.Signals(), action: static signal => Assert.Equal(expected: SweepStatus.Clean, actual: signal.Status));

            // The renderer a headless World lacks is a refusal the settle reports red, never a wait it passes.
            Assert.Equal(expected: SweepStatus.Red, actual: run.Settle(deadline: TimeSpan.FromSeconds(value: 1))!.Status);

            // A stop whose motion is pinned to ticks still arms and reaches its capture, which a World with no renderer refuses.
            var tour = run.Tour(names: []);

            Assert.Equal(expected: SweepStatus.Red, actual: Assert.Single(collection: tour.Stills).Status);
            Assert.Contains(collection: tour.Stills[0].Reasons, filter: static reason => reason.StartsWith(comparisonType: StringComparison.Ordinal, value: "capture refused"));
            // A stop's long tick wait runs as several short calls and none is refused.
            Assert.DoesNotContain(collection: tour.Stills[0].Reasons, filter: static reason => reason.Contains(value: "world.wait 30", comparisonType: StringComparison.Ordinal) || reason.Contains(value: "world.wait 10", comparisonType: StringComparison.Ordinal));
            Assert.Equal(expected: ["watchdog.after-tour"], actual: run.Recheck().Select(selector: static signal => signal.Name));

            cancel.Cancel();

            Assert.Equal(expected: SweepStatus.Unknown, actual: Assert.Single(collection: run.Tools()).Status);
            Assert.All(collection: run.Signals(), action: static signal => Assert.Equal(expected: SweepStatus.Unknown, actual: signal.Status));
            Assert.Equal(expected: SweepStatus.Unknown, actual: Assert.Single(collection: run.Tour(names: []).Stills).Status);
            Assert.Contains(expectedSubstring: "cancelled", actualString: budget.Reason);
        }

        _ = Assert.Throws<ArgumentException>(testCode: () => System.Diagnostics.Process.GetProcessById(processId: processId));
    }
    [Fact]
    public void ALineThatPassesItsDeadlineIsUnmeasuredAndTheNextLineAnswersOnTheSameWorld() {
        using var budget = new SweepBudget(TimeSpan.FromMinutes(minutes: 3), CancellationToken.None);
        using var world = Boot(budget: budget, name: "line-deadline");
        // The World is paused, so a long tick wait cannot finish inside the line's one-second deadline.
        var slow = world.Run(script: "world.wait 100000", timeoutSeconds: 1);

        Assert.False(condition: slow.Ok);
        Assert.True(condition: slow.Unmeasured);
        Assert.Contains(actualString: slow.Output, expectedSubstring: "deadline");
        Assert.False(condition: world.Gone);

        var next = world.Run(script: "world.counters state");

        Assert.True(condition: next.Ok, userMessage: next.Output);
        Assert.NotEmpty(collection: next.Output);
    }
    [Fact]
    public void AWorldThatNeverAttachesReleasesItsLease() {
        using var budget = new SweepBudget(caller: CancellationToken.None, total: TimeSpan.FromMinutes(value: 10));
        var lease = new Lease();

        Assert.False(condition: SweepWorld.TryBoot(arguments: Arguments(state: "never"), artifact: m_entry, bootTimeout: TimeSpan.Zero, budget: budget, error: out var error, lease: lease, logDirectory: Path.Combine(path1: m_directory, path2: "never"), session: out var world, workingDirectory: m_root));
        Assert.Null(@object: world);
        Assert.Contains(actualString: error, expectedSubstring: "never published");
        Assert.True(condition: lease.Released);
    }
    [Fact]
    public void AWorldStartsPausedAndScheduledRefusalsAreObservedAfterArmingSucceeded() {
        using var budget = new SweepBudget(TimeSpan.FromMinutes(minutes: 2), CancellationToken.None);
        using var world = Boot(budget: budget, name: "scheduled-refusal");

        AssertTick(expected: 0);
        var applied = world.Run("world.state.cell.set fuzzer seed 12345");

        Assert.True(condition: applied.Ok, userMessage: string.Join(separator: "; ", values: applied.Refusals));
        Assert.Contains("world.state.cell.set:", applied.Output);
        var refused = world.Run("world.state.cell.set no-such-row seed 1");

        Assert.False(condition: refused.Ok);
        Assert.NotEmpty(collection: refused.Output);
        // Coordinator delay and immediate reads cannot advance the boot authority.
        _ = world.Run("world.counters state");
        AssertTick(expected: 0);
        Assert.True(condition: world.Run("world.at 3 body.stop 999999").Ok);
        Assert.True(condition: world.Run("world.rate resume\nworld.wait 10\nworld.rate pause").Ok);
        var evidence = SweepEvidence.Scheduled(world.Run("world.at --json"), [(3UL, "body.stop 999999")]);

        Assert.False(condition: evidence.Ok);
        Assert.Contains(collection: evidence.Refusals, filter: reason => reason.Contains(comparisonType: StringComparison.Ordinal, value: "refused"));

        void AssertTick(ulong expected) {
            var answer = world.Run("world.at --json");

            Assert.True(condition: answer.Ok, userMessage: answer.Output);
            using var json = JsonDocument.Parse(answer.Output);

            Assert.Equal(expected, json.RootElement.GetProperty(propertyName: "tick").GetUInt64());
        }
    }
    [Fact]
    public void ALogOpenFailureStartsNoChildAndReleasesTheLease() {
        var folder = Path.Combine(path1: m_directory, path2: "bad-log");

        _ = Directory.CreateDirectory(path: Path.Combine(path1: folder, path2: "stderr.log"));
        using var budget = new SweepBudget(TimeSpan.FromMinutes(minutes: 2), CancellationToken.None);
        var lease = new Lease();
        // stdout is opened first; failure opening stderr must also close stdout.
        Assert.Throws<UnauthorizedAccessException>(testCode: () => SweepWorld.TryBoot(m_entry, Arguments(state: "bad-log"), m_root, folder, lease,
            TimeSpan.FromMinutes(minutes: 1), budget, out _, out _));
        Assert.True(condition: lease.Released);
        using var reopened = new FileStream(Path.Combine(path1: folder, path2: "stdout.log"), FileMode.Open, FileAccess.Write, FileShare.None);
    }
    [Fact]
    public void ThePublicationWaitCancelsAndItsOwnedProcessStops() {
        int pid;

        using (var process = MotionWorldProcess.Start(m_entry, Arguments(state: "cancel-wait"), m_root, Path.Combine(path1: m_directory, path2: "cancel-wait"))) {
            pid = process.ProcessId;
            using var cancel = new CancellationTokenSource(delay: TimeSpan.FromMilliseconds(milliseconds: 100));
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.Null(@object: process.WaitFor(0, _ => false, TimeSpan.FromMinutes(minutes: 5), cancel.Token));
            Assert.True(condition: cancel.IsCancellationRequested);
            Assert.True(condition: (clock.Elapsed < TimeSpan.FromSeconds(seconds: 5)));
        }
        Assert.Throws<ArgumentException>(testCode: () => System.Diagnostics.Process.GetProcessById(processId: pid));
    }
}
