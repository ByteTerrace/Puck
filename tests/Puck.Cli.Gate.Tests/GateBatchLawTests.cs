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

/// <summary><see cref="GateRun"/> runs the device suites and the citations after affected only with the GPU, launches no build that leaves an MSBuild node behind, and flushes its step summary as it goes.</summary>
public sealed class GateBatchLawTests : GateRunLaws {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DeviceSuitesThenCitationsFollowAffectedOnlyWithGpu(bool gpu) {
        using var branches = new Branches();

        using var directory = new TemporaryDirectory(prefix: "puck-gate-batch-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: gpu).ExitCode);
        var deviceStart = runner.Events.IndexOf(item: "run Puck.World.Tests");

        if (!gpu) {
            Assert.Empty(collection: runner.Devices);
            Assert.DoesNotContain(collection: runner.Steps, filter: step => step.SequenceEqual(other: ["docs", "citations"]));
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
        Assert.Equal(new[] { "run Puck.World.Tests", "run Puck.DirectX.Tests", "run Puck.Vulkan.Tests", "run Puck.Platform.Windows.Tests", "run docs citations", "run verbs" },
            runner.Events.Skip(count: deviceStart).Where(predicate: entry => entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "run ")));
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
        Assert.Equal(actual: CliTestRun.CpuSelection, expected: ["--filter-not-trait", "Category=Gpu"]);
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
/// <summary><see cref="GateRun"/> consults admission immediately before each heavy step, waits for the GPU only on device steps, and refuses before starting a step whose admission timed out.</summary>
public sealed class GateAdmissionLawTests : GateRunLaws {
    [Fact]
    public void EveryHeavyStepAndNoLightStepConsultsAdmissionImmediatelyBeforeRunning() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-admission-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: true).ExitCode);
        string[] heavy = ["build", "affected", "verbs", "Puck.DirectX.Tests", "Puck.Vulkan.Tests", "Puck.Platform.Windows.Tests", "docs citations", "verbs windowed"];

        Assert.Equal(heavy.Select(selector: name => ("admit " + name)), runner.Events.Where(predicate: entry => entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "admit ")));
        for (var index = 0; (index < runner.Events.Count); index++) {
            if (runner.Events[index].StartsWith(comparisonType: StringComparison.Ordinal, value: "admit ")) {
                Assert.StartsWith(expectedStartString: "run ", actualString: runner.Events[(index + 1)]);
            }
        }
    }
    [Fact]
    public void OnlyADeviceStepWaitsForAnIdleGpuAndOnlyAHeavySuiteForAnotherHeavyRun() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-admission-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: ""));

        Assert.Equal(CliExit.Success, Gate(branches, runner, directory, gpu: true).ExitCode);
        // Heavy device-suite admission is inside the process runner's lease, not this preflight.
        Assert.Equal(expected: [("build", false, false), ("affected", false, false), ("verbs", false, false), ("Puck.DirectX.Tests", true, false), ("Puck.Vulkan.Tests", true, false), ("Puck.Platform.Windows.Tests", true, false), ("docs citations", true, false), ("verbs windowed", true, false)], actual: runner.Admissions);
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
