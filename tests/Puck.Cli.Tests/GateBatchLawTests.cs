using System.CommandLine;
using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Cli.Gate;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

internal sealed class GateClock : TimeProvider {
    private long m_ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => m_ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks: m_ticks);
    public void Advance(TimeSpan duration) => m_ticks += duration.Ticks;
}

public sealed partial class GateRunLawTests {
    private static void Workload(Branches branches, string name, bool ceilings = true, bool script = false) {
        branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.world.json", text: "{}");
        if (ceilings) { branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.ceilings.json", text: "{}"); }
        if (script) { branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.script.txt", text: "world.counters --json"); }
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DeviceSuitesThenEveryRecordedWorkloadThenCitationsFollowAffectedOnlyWithGpu(bool gpu) {
        using var branches = new Branches();

        Workload(branches, "z", script: true);
        Workload(branches, "a");
        Workload(branches, "unrecorded", ceilings: false, script: true);
        using var directory = new TemporaryDirectory(prefix: "puck-gate-batch-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: gpu).ExitCode);
        var deviceStart = runner.Events.IndexOf(item: "run Puck.World.Tests");

        if (!gpu) {
            Assert.Empty(collection: runner.Devices);
            Assert.DoesNotContain(collection: runner.Steps, filter: step => ((step[0] == "counters") || step.SequenceEqual(other: ["docs", "citations"])));
            return;
        }
        Assert.True(condition: (deviceStart > runner.Events.IndexOf(item: "run affected")));
        Assert.DoesNotContain("--gpu", runner.Steps[0]);
        Assert.Equal(new[] { "Puck.World.Tests", "Puck.DirectX.Tests", "Puck.Vulkan.Tests", "Puck.Platform.Windows.Tests" },
            runner.Devices.Select(selector: arguments => Path.GetFileNameWithoutExtension(path: arguments[2])));
        foreach (var arguments in runner.Devices) {
            var suite = Path.GetFileNameWithoutExtension(path: arguments[2]);

            Assert.Equal([.. AffectedCommand.TestArguments(suite: suite), .. GatePlan.DeviceSuites.Single(predicate: device => (device.Suite == suite)).Selection], arguments);
        }
        Assert.Equal(new[] { "run Puck.World.Tests", "run Puck.DirectX.Tests", "run Puck.Vulkan.Tests", "run Puck.Platform.Windows.Tests", "run counters a", "run counters z", "run docs citations" },
            runner.Events.Skip(count: deviceStart).Where(predicate: entry => entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "run ")));
        Assert.All(runner.Steps.Where(predicate: step => (step[0] == "counters")), step => Assert.Contains(collection: step, expected: "--check"));
    }
    [Fact]
    public void EveryBuildAndTestTheGateAndAffectedLaunchLeavesNoMSBuildNodeBehind() {
        Assert.Equal(actual: CliOptions.NoNodeReuse, expected: "-nodeReuse:false");
        Assert.Contains(collection: GatePlan.Steps.Single(predicate: static step => (step.Kind == GateStepKind.Build)).Arguments, expected: CliOptions.NoNodeReuse);
        // A suite run takes no MSBuild switch, which the test application refuses: it runs the binaries a build wrote.
        Assert.All(collection: GatePlan.Steps.Where(predicate: static step => (step.Kind == GateStepKind.DeviceSuite)), action: static step => {
            Assert.Contains(collection: step.Arguments, expected: "--no-build");
            Assert.DoesNotContain(collection: step.Arguments, expected: CliOptions.NoNodeReuse);
        });
        Assert.Contains(collection: AffectedCommand.TestArguments(suite: "Puck.Cli.Tests"), expected: "--no-build");
        Assert.DoesNotContain(collection: AffectedCommand.TestArguments(suite: "Puck.Cli.Tests"), expected: CliOptions.NoNodeReuse);
        Assert.Contains(collection: AffectedCommand.BuildArguments(suite: "Puck.Cli.Tests"), expected: CliOptions.NoNodeReuse);
        Assert.Contains(collection: CliProjectBuild.Arguments(project: "src/Puck.World/Puck.World.csproj"), expected: CliOptions.NoNodeReuse);
    }
    [Fact]
    public void EveryDeviceSuiteRunsTheGpuTraitAndTheCpuRunsItsComplement() {
        Assert.All(collection: GatePlan.DeviceSuites, action: static device => Assert.Equal(actual: device.Selection, expected: ["--filter-trait", "Category=Gpu"]));
        Assert.Equal(actual: AffectedCommand.CpuSelection, expected: ["--filter-not-trait", "Category=Gpu"]);
    }
    [Fact]
    public void EveryPuckStepThePlanExpandsParsesThroughTheRootCommand() {
        using var branches = new Branches();

        Workload(branches, "a", script: true);
        Workload(branches, "b");
        var affected = new AffectedPlan(Baselines: BaselinesCommand.Artifacts, Canaries: ["example"], CanaryChecks: [], Catalog: false, Deleted: [], Everything: true, Parity: true, Suites: [], Unmapped: [], Worlds: []);
        var steps = GatePlan.Expand(affected: affected, fileList: "files.json", gpu: true, mergeBase: "HEAD", record: true, repositoryRoot: branches.Checkout.Root, sources: true)
            .Where(predicate: static step => (step.Kind is GateStepKind.Puck or GateStepKind.Baseline or GateStepKind.Canaries or GateStepKind.Parity))
            .ToArray();

        Assert.Contains(collection: steps, filter: static step => (step.Arguments[0] == "counters"));
        foreach (var step in steps) {
            var parsed = PuckRootCommand.Create(clock: TimeProvider.System).Parse(args: step.Arguments);

            Assert.True(condition: (parsed.Errors.Count == 0),
                userMessage: $"puck {string.Join(separator: ' ', value: step.Arguments)}: {string.Join(separator: "; ", values: parsed.Errors.Select(selector: static error => error.Message))}");
        }
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void RecordRequiresGpuAndEveryEarlierStepToPass(bool gpu, bool fails) {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-record-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: "")) { ExitCode = args => ((fails && (args[0] == "lengths")) ? 1 : 0) };
        var result = Gate(branches, runner, directory, gpu: gpu, record: true);

        Assert.Equal(actual: result.ExitCode, expected: (!gpu ? CliExit.Refused : (fails ? CliExit.Failed : CliExit.Success)));
        if (!gpu) { Assert.Empty(collection: runner.Events); Assert.Contains(actualString: result.Error, expectedSubstring: "requires --gpu"); }
        Assert.Equal(((gpu && !fails) ? 1 : 0), runner.Steps.Count(predicate: step => step.SequenceEqual(other: ["affected", "--record"])));
        if (gpu && !fails) { Assert.Equal(["affected", "--record"], runner.Steps[^1]); }
        var refused = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["gate", "--record"]));

        Assert.Equal(actual: refused.ExitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: refused.Error, expectedSubstring: "requires --gpu");
    }
    [Fact]
    public void RecordIsWithheldForEachPossibleFailedQualificationStep() {
        using var branches = new Branches();

        Workload(branches, "a");
        branches.Checkout.Write(name: "build/trigger.props", text: "<Project />");
        branches.Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n\n");
        foreach (var step in GatePlan.Steps.Where(predicate: step => ((step.Kind != GateStepKind.CopyCli) && !step.Record))) {
            using var directory = new TemporaryDirectory(prefix: "puck-gate-record-failure-law-");
            var runner = new FakeRunner(build: new GateStepResult(ExitCode: ((step.Kind == GateStepKind.Build) ? 1 : 0), Output: "")) {
                ExitCode = args => ((args.TakeWhile(predicate: arg => !arg.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")).First() == step.Arguments[0]) ? 1 : 0),
            };

            Assert.Equal(CliExit.Failed, Gate(branches, runner, directory, gpu: true, record: true).ExitCode);
            Assert.DoesNotContain(collection: runner.Steps, filter: args => args.Contains(value: "--record"));
        }
    }
    [Fact]
    public void EveryHeavyStepAndNoLightStepConsultsAdmissionImmediatelyBeforeRunning() {
        using var branches = new Branches();

        Workload(branches, "a");
        using var directory = new TemporaryDirectory(prefix: "puck-gate-admission-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: true, record: true).ExitCode);
        string[] heavy = ["build", "affected", "Puck.World.Tests", "Puck.DirectX.Tests", "Puck.Vulkan.Tests", "Puck.Platform.Windows.Tests", "counters a", "docs citations", "affected record"];

        Assert.Equal(heavy.Select(selector: name => ("admit " + name)), runner.Events.Where(predicate: entry => entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "admit ")));
        foreach (var name in heavy) { Assert.Equal(("run " + name), runner.Events[(runner.Events.IndexOf(item: ("admit " + name)) + 1)]); }
    }
    [Fact]
    public void OnlyADeviceStepWaitsForAnIdleGpuAndOnlyAHeavySuiteForAnotherHeavyRun() {
        using var branches = new Branches();

        Workload(branches, "a");
        using var directory = new TemporaryDirectory(prefix: "puck-gate-admission-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: true, record: true).ExitCode);
        Assert.Equal(expected: [("build", false, false), ("affected", false, false), ("Puck.World.Tests", true, true), ("Puck.DirectX.Tests", true, false), ("Puck.Vulkan.Tests", true, false), ("Puck.Platform.Windows.Tests", true, false), ("counters a", true, false), ("docs citations", true, false), ("affected record", true, false)], actual: runner.Admissions);
        // The baselines and affected's suites run CPU tests alone: none of their classes carries the Gpu trait.
        Assert.All(collection: GatePlan.Steps.Where(predicate: static step => ((step.Kind is GateStepKind.Build or GateStepKind.Baseline) || (step.Name == "affected"))), action: static step => Assert.False(condition: step.Gpu));
    }
    [Fact]
    public void AdmissionTimeoutRefusesBeforeStartingTheStep() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-admission-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: "")) { Admitted = false };

        Assert.Equal(CliExit.Refused, Gate(branches, runner, directory).ExitCode);
        Assert.Equal(["admit build"], runner.Events);
        Assert.Empty(collection: File.ReadAllLines(path: directory.PathOf(name: "gate.steps")));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void StepSummaryFlushesOneStartAndExitPerExecutedStepWithHostClockTimes(bool failedBuild) {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-summary-law-");
        var summary = directory.PathOf(name: "gate.steps");
        var snapshots = new List<(string Name, string[] Lines)>();
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: (failedBuild ? 1 : 0), Output: "")) {
            Executing = name => {
                using var reader = new StreamReader(stream: new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: summary, share: FileShare.ReadWrite));
                var current = reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: line => line.TrimEnd(trimChar: '\r')).ToArray();

                snapshots.Add(item: (name, current));
            },
        };
        var result = Gate(branches, runner, directory);

        Assert.Equal(actual: result.ExitCode, expected: (failedBuild ? CliExit.Failed : CliExit.Success));
        Assert.Contains(actualString: result.Output, expectedSubstring: "gate.steps");
        foreach (var (name, current) in snapshots) {
            Assert.NotEmpty(collection: current);
            Assert.EndsWith($"start {name} exit=- elapsed=0s", current[^1]);
            Assert.Equal(1, (current.Length % 2));
        }
        var lines = File.ReadAllLines(path: summary);
        var names = runner.Events.Where(predicate: entry => entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "run ")).Select(selector: entry => entry[4..]).ToArray();

        Assert.Equal((names.Length * 2), lines.Length);
        for (var index = 0; (index < names.Length); index++) {
            var start = DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds: (2700 * index));

            Assert.Equal($"{start:O} start {names[index]} exit=- elapsed=0s", lines[(2 * index)]);
            Assert.Equal($"{start.AddMilliseconds(milliseconds: 2700):O} exit {names[index]} exit={(failedBuild ? 1 : 0)} elapsed=2s", lines[((2 * index) + 1)]);
        }
    }
}
public sealed class GatePlanLawTests {
    private static IEnumerable<(string Path, Command Command)> Commands(Command parent, string prefix = "") {
        foreach (var command in parent.Subcommands) {
            var path = (prefix + command.Name);

            yield return (path, command);
            foreach (var nested in Commands(parent: command, prefix: (path + " "))) { yield return nested; }
        }
    }

    [Fact]
    public void EveryCheckInTheEntireRootTreeHasAGateStepOrAReasonedExclusion() {
        var checks = Commands(PuckRootCommand.Create(clock: TimeProvider.System)).Where(predicate: entry => entry.Command.Options.Any(predicate: option => ((option.Name == "--check") || option.Aliases.Contains(item: "--check")))).Select(selector: entry => entry.Path).ToHashSet(comparer: StringComparer.Ordinal);
        var included = GatePlan.Steps.Where(predicate: step => step.Arguments.Contains(value: "--check")).Select(selector: step => string.Join(separator: ' ', values: step.Arguments.TakeWhile(predicate: argument => !argument.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")))).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var check in checks) { Assert.True(condition: (included.Contains(item: check) || GatePlan.CheckExclusions.ContainsKey(key: check)), userMessage: $"puck {check} --check needs a gate step or an explicit reasoned exclusion in GatePlan."); }
        foreach (var (excluded, reason) in GatePlan.CheckExclusions) {
            Assert.Contains(expected: excluded, set: checks);
            Assert.DoesNotContain(expected: excluded, set: included);
            Assert.False(condition: string.IsNullOrWhiteSpace(value: reason));
        }
        Assert.All(included, check => Assert.Contains(expected: check, set: checks));
        foreach (var artifact in BaselinesCommand.Artifacts) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: artifact.Project));
            Assert.NotEmpty(collection: artifact.Inputs);
            Assert.All(collection: artifact.Inputs, action: static input => Assert.False(condition: string.IsNullOrWhiteSpace(value: input)));
            Assert.Contains(collection: GatePlan.Steps, filter: step => ((step.Kind == GateStepKind.Baseline) && step.Arguments.SequenceEqual(other: artifact.CheckArguments())));
        }
    }
    [Fact]
    public void DiscoveryRequiresCeilingsUsesOptionalOrRecordedScriptsAndSortsWorkloads() {
        using var directory = new TemporaryDirectory(prefix: "puck-gate-discovery-law-");

        directory.WriteText(name: "tests/Puck.Counters/unrecorded.world.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/unrecorded.script.txt", text: "script");
        directory.WriteText(name: "tests/Puck.Counters/z.world.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/z.ceilings.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/z.script.txt", text: "script");
        directory.WriteText(name: "tests/Puck.Counters/a.world.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/a.ceilings.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/shared.world.json", text: "{}");
        directory.WriteText(name: "tests/Puck.Counters/shared.ceilings.json", text: "{\"script\":\"tests/Puck.Counters/common.script.txt\"}");
        var workloads = GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: GatePlan.Steps.Single(predicate: step => (step.Kind == GateStepKind.Counters))).ToArray();

        Assert.Equal(["counters a", "counters shared", "counters z"], workloads.Select(selector: step => step.Name));
        Assert.Equal(["counters", "--check", "--world", "tests/Puck.Counters/a.world.json", "--ceilings", "tests/Puck.Counters/a.ceilings.json"], workloads[0].Arguments);
        Assert.Equal(["--script", "tests/Puck.Counters/common.script.txt"], workloads[1].Arguments.TakeLast(count: 2));
        Assert.Equal(["--script", "tests/Puck.Counters/z.script.txt"], workloads[2].Arguments.TakeLast(count: 2));
    }
    [Fact]
    public void TheRenderedHelpAndCliReferenceContainTheWholePlanInOrder() {
        var command = GateCommand.Create(clock: TimeProvider.System);

        Assert.Contains(GatePlan.Detail(), CliHelp.DetailOf(command: command));
        var help = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["gate", "--help"]));

        Assert.Equal(actual: help.ExitCode, expected: 0);
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        var reference = File.ReadAllText(path: Path.Combine(path1: root, path2: "docs/reference/cli.md"));
        var begin = reference.IndexOf(comparisonType: StringComparison.Ordinal, value: "## `puck gate`");
        var end = reference.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (begin + 1), value: "\n## ");
        var section = reference[begin..end];
        var helpPosition = 0;
        var docPosition = 0;

        foreach (var step in GatePlan.Steps) {
            helpPosition = help.Output.IndexOf((step.Name + ":"), helpPosition, StringComparison.Ordinal);
            docPosition = section.IndexOf((("`" + step.Name) + "`"), docPosition, StringComparison.Ordinal);
            Assert.True(condition: (helpPosition >= 0), userMessage: $"help lacks ordered step {step.Name}");
            Assert.True(condition: (docPosition >= 0), userMessage: $"cli.md gate section lacks ordered step {step.Name}");
            helpPosition += step.Name.Length;
            docPosition += (step.Name.Length + 2);
        }
    }
}
