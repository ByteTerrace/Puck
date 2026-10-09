using System.CommandLine;
using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

internal sealed class GateClock : TimeProvider {
    private long m_ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => m_ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks: m_ticks);
    public void Advance(TimeSpan duration) => m_ticks += duration.Ticks;
}

/// <summary>The counters workloads the batch laws write into their scratch checkouts.</summary>
public abstract class GateBatchLaws : GateRunLaws {
    private protected static void Workload(Branches branches, string name, bool ceilings = true, bool script = false) {
        branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.world.json", text: "{}");
        if (ceilings) { branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.ceilings.json", text: $$"""{"workload":"tests/Puck.Counters/{{name}}.world.json"}"""); }
        if (script) { branches.Checkout.Write(name: $"tests/Puck.Counters/{name}.script.txt", text: "world.counters --json"); }
    }
}
/// <summary><see cref="GateRun"/> runs the device suites, every recorded workload and the citations after affected only with the GPU, launches no build that leaves an MSBuild node behind, and flushes its step summary as it goes.</summary>
public sealed class GateBatchLawTests : GateBatchLaws {
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
            Assert.Equal($"{start.AddMilliseconds(milliseconds: 2700):O} exit {names[index]} exit={((failedBuild && (names[index] == "build")) ? 1 : 0)} elapsed=2s", lines[((2 * index) + 1)]);
        }
    }
}
/// <summary><see cref="GateRun"/> records only with the GPU and only once every earlier qualification step passed.</summary>
public sealed class GateRecordLawTests : GateBatchLaws {
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
        var refused = ConsoleCapture.RunSplit(run: () => SuiteRoot.Invoke(args: ["gate", "--record"]));

        Assert.Equal(actual: refused.ExitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: refused.Error, expectedSubstring: "requires --gpu");
    }
    [Fact]
    public void RecordIsWithheldForEachPossibleFailedQualificationStep() {
        using var branches = new Branches();

        Workload(branches, "a");
        branches.Checkout.Write(name: "build/trigger.props", text: "<Project />");
        branches.Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n\n");
        // The change is the same for every failure, so it is read once; each failure executes it under its own runner.
        var change = Plan(branches: branches);

        foreach (var step in GatePlan.Steps.Where(predicate: step => ((step.Kind != GateStepKind.CopyCli) && !step.Record))) {
            using var directory = new TemporaryDirectory(prefix: "puck-gate-record-failure-law-");
            var runner = new FakeRunner(build: new GateStepResult(ExitCode: ((step.Kind == GateStepKind.Build) ? 1 : 0), Output: "")) {
                ExitCode = args => ((args.TakeWhile(predicate: arg => !arg.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")).First() == step.Arguments[0]) ? 1 : 0),
            };

            Assert.Equal(CliExit.Failed, Execute(branches, change, runner, directory, gpu: true, record: true).ExitCode);
            Assert.DoesNotContain(collection: runner.Steps, filter: args => args.Contains(value: "--record"));
        }
    }
}
/// <summary><see cref="GateRun"/> consults admission immediately before each heavy step, waits for the GPU only on device steps, and refuses before starting a step whose admission timed out.</summary>
public sealed class GateAdmissionLawTests : GateBatchLaws {
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
        // The locked restore is light and runs first; the build is the first step admission holds.
        Assert.Equal(["run locks", "admit build"], runner.Events);
        Assert.DoesNotContain(collection: File.ReadAllLines(path: directory.PathOf(name: "gate.steps")), filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: " build "));
    }
}
