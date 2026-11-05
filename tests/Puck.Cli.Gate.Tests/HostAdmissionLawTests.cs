using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HostAdmissionLawTests {
    // The 16 GB, six-thread developer machine: capacity above 5 GB free, pressure below 2 GB, disk pressure below 10 GB.
    private static readonly HostLoadThresholds Thresholds = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6);

    // A commit reading is a percentage of a 40 GB commit limit; none is a platform that reports no limit.
    private static HostSample Sample(double cpu = 0, double ram = 8, double disk = 100, string? gpu = null, string? heavy = null, double? commit = null) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, HeavyTestHolder: heavy, ReuseNodes: 42, CommitChargeGb: ((commit is { } percent) ? (percent * 0.4) : double.NaN), CommitLimitGb: ((commit is null) ? double.NaN : 40));

    [Fact]
    public void HostOptionsDefaultToTheAdmissionThresholdsAndStillAllowOverrides() {
        var load = HostCommand.Create(composition: SuiteRoot.Composition).Subcommands.Single(predicate: command => (command.Name == "load"));
        var defaults = HostLoadThresholds.ThisMachine;
        (string Name, double? Value)[] thresholds = [("--capacity-cpu", defaults.CapacityCpuPercent), ("--capacity-ram", defaults.CapacityRamGb), ("--capacity-commit", defaults.CapacityCommitPercent),("--pressure-ram", defaults.PressureRamGb), ("--pressure-disk", defaults.PressureDiskGb)];

        foreach (var (name, value) in thresholds) {
            var option = ((Option<double?>)load.Options.Single(predicate: option => (option.Name == name)));

            Assert.Equal(value, load.Parse([]).GetValue(option: option));
            Assert.Equal(1, load.Parse([name, "1"]).GetValue(option: option));
        }
        Assert.Contains("Defaults scale with the machine", CliHelp.DetailOf(command: load));
    }
    [Fact]
    public void AdmissionJudgesFreeMemoryAgainstTheMachinesOwnSize() {
        var clock = new GateClock();
        var calls = 0;
        using var small = new StringWriter();
        using var large = new StringWriter();

        // 8 GB free is headroom on a 16 GB machine and not on a 32 GB one, which waits until 12 GB are free.
        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: 8), clock, _ => Assert.Fail(message: "8 GB free must admit on a 16 GB machine"), small, CancellationToken.None, Thresholds));
        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: ((calls++ == 0) ? 8 : 12)), clock, clock.Advance, large, CancellationToken.None, HostLoadThresholds.For(installedRamGb: 32, logicalProcessors: 16)));
        Assert.Equal(actual: calls, expected: 2);
        Assert.Contains(actualString: large.ToString(), expectedSubstring: "freeRAM=8.0GB (admission needs over 10.0GB)");
    }
    [Fact]
    public void IdleAdmissionHasNoWaitOrRefusalEvenWithReuseNodes() {
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(), new GateClock(), _ => Assert.Fail(message: "idle must not wait"), error, CancellationToken.None, Thresholds));
        Assert.Equal("", error.ToString());
    }
    [InlineData("ram")]
    [InlineData("disk")]
    [InlineData("gpu")]
    [Theory]
    public void CapacityWaitsVisiblyThenReturnsRegardlessOfLineThrottling(string pressure) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();
        var busy = pressure switch {
            "ram" => Sample(ram: 1),
            "disk" => Sample(disk: 1),
            _ => Sample(gpu: "Puck.World 42"),
        };

        Assert.True(condition: HostAdmission.Wait("step", true, false, () => ((++calls <= 2) ? busy : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(actual: calls, expected: 3);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 20), clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Contains("waiting for host capacity before step", error.ToString());
        Assert.Contains("capacity returned for step", error.ToString());
        Assert.Equal(2, error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Length);
    }
    [Fact]
    public void AWaitNamesWhatHoldsItBackAndEachNewGpuHolder() {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();
        HostSample[] readings = [Sample(gpu: "testhost 77"), Sample(gpu: "testhost 77"), Sample(gpu: "Puck.World 9"), Sample(ram: 1), Sample()];

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => readings[calls++], clock, clock.Advance, error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

        Assert.Equal(expected: 3, actual: lines.Length);
        Assert.EndsWith(actualString: lines[0].TrimEnd(), expectedEndString: "the GPU is held by testhost 77.");
        Assert.EndsWith(actualString: lines[1].TrimEnd(), expectedEndString: "the GPU is held by Puck.World 9.");
        Assert.Contains(actualString: lines[2], expectedSubstring: "capacity returned for Puck.World.Tests");
        Assert.Contains(actualString: HostAdmissionReason(ram: 1), expectedSubstring: "no memory headroom: freeRAM=1.0GB");
    }
    [Fact]
    public void OnlyADeviceStepWaitsForTheGpuHolder() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("baselines browser-parity", false, false, () => Sample(gpu: "Puck.World.Tests 7"), clock, _ => Assert.Fail(message: "a CPU step must not wait on the GPU"), error, CancellationToken.None, Thresholds));
        Assert.Equal(expected: "", actual: error.ToString());

        var calls = 0;

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => ((calls++ == 0) ? Sample(gpu: "Puck.World 9") : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "the GPU is held by Puck.World 9");

        // A CPU step still waits for memory headroom, and names it rather than a GPU holder.
        using var busy = new StringWriter();

        calls = 0;
        Assert.True(condition: HostAdmission.Wait("affected", false, false, () => ((calls++ == 0) ? Sample(ram: 1, gpu: "Puck.World 9") : Sample(gpu: "Puck.World 9")), clock, clock.Advance, busy, CancellationToken.None, Thresholds));
        Assert.Contains(actualString: busy.ToString(), expectedSubstring: "no memory headroom: freeRAM=1.0GB");
        Assert.DoesNotContain(expectedSubstring: "GPU", actualString: busy.ToString());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CpuLoadIsAdvisoryAndNeverHoldsOrRefusesAStep(bool device) {
        var clock = new GateClock();
        using var error = new StringWriter();

        // A CPU that never falls under the threshold, with memory, disk and the GPU free.
        Assert.True(condition: HostAdmission.Wait("build", device, false, () => Sample(cpu: 99), clock, _ => Assert.Fail(message: "CPU load must not hold a step back"), error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

        Assert.Single(collection: lines);
        Assert.Contains(actualString: lines[0], expectedSubstring: "build runs under cpu=99%");
        Assert.DoesNotContain(expectedSubstring: "waiting", actualString: lines[0]);
    }
    [Fact]
    public void AHeavySuiteWaitsOnAnotherHeavyRunPastTheOrdinaryBoundNamingEachHolderWithHeartbeats() {
        var clock = new GateClock();
        using var error = new StringWriter();

        // Another gate's Puck.World.Tests holds the machine for 35 minutes, a third one's for 15 more, then none.
        HostSample Reading() => clock.GetElapsedTime(startingTimestamp: 0).TotalMinutes switch {
            < 35 => Sample(heavy: "Puck.World.Tests 11"),
            < 50 => Sample(heavy: "Puck.World.Tests 12"),
            _ => Sample(),
        };

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", false, true, Reading, clock, clock.Advance, error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: static line => line.TrimEnd()).ToArray();

        Assert.Equal(actual: lines, expected: [
            "gate: waiting for host capacity before Puck.World.Tests (at most 120 minutes): a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 10 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 20 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 30 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: waiting for host capacity before Puck.World.Tests (at most 120 minutes): a heavy test run is held by Puck.World.Tests 12.",
            "gate: still waiting before Puck.World.Tests after 45 minutes: a heavy test run is held by Puck.World.Tests 12.",
            "gate: capacity returned for Puck.World.Tests.",
        ]);
        Assert.Equal(expected: TimeSpan.FromMinutes(minutes: 50), actual: clock.GetElapsedTime(startingTimestamp: 0));

        // A step that runs no heavy suite never waits on one.
        using var light = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("baselines state", false, false, () => Sample(heavy: "Puck.World.Tests 11"), clock, _ => Assert.Fail(message: "a light step must not wait on a heavy run"), light, CancellationToken.None, Thresholds));
        Assert.Equal(expected: "", actual: light.ToString());
    }
    [Fact]
    public void AHeavySuiteIsRefusedOnlyAtItsTwoHourBound() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("Puck.World.Tests", false, true, () => Sample(heavy: "Puck.World.Tests 11"), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(expected: TimeSpan.FromHours(hours: 2), actual: clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Equal(expected: CliLease.DefaultWait, actual: TimeSpan.FromHours(hours: 2));
    }

    private static string HostAdmissionReason(double ram) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        // Too little memory first, then headroom.
        _ = HostAdmission.Wait("x", true, false, () => ((calls++ == 0) ? Sample(ram: ram) : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds);
        return error.ToString();
    }

    // A 90% commit reading holds a step back while 8 GB of RAM and 100 GB of disk are free, and says so; a 60% one admits
    // at once. The stage a caller shows is the phrase "waiting for memory headroom: commit 90% of limit".
    [Fact]
    public void ACommitChargeOverItsShareOfTheLimitHoldsAStepBackAndIsNamedAndUnderItAdmits() {
        var clock = new GateClock();
        var calls = 0;
        var stages = new List<string>();
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("puck_world_start", false, false, () => ((calls++ < 2) ? Sample(commit: 90) : Sample(commit: 60)), clock, clock.Advance, error, CancellationToken.None, Thresholds, stage: stages.Add));
        Assert.Equal(expected: 3, actual: calls);
        Assert.Equal(expected: ["waiting for memory headroom: commit 90% of limit", "waiting for memory headroom: commit 90% of limit"], actual: stages);
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit 90% of limit (admission needs under 75%).");
        Assert.DoesNotContain(expectedSubstring: "freeRAM", actualString: error.ToString());

        using var idle = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("puck_world_start", false, false, () => Sample(commit: 60), clock, _ => Assert.Fail(message: "60% commit must admit"), idle, CancellationToken.None, Thresholds));
        Assert.Equal(expected: "", actual: idle.ToString());
        // A platform that reports no commit limit is never held back by it; 75% itself is not under the share.
        Assert.True(condition: HostAdmission.Wait("x", false, false, () => Sample(), clock, _ => Assert.Fail(message: "no commit reading must admit"), idle, CancellationToken.None, Thresholds));
        var bounded = new GateClock();

        Assert.False(condition: HostAdmission.Wait("x", false, false, () => Sample(commit: 75), bounded, bounded.Advance, idle, CancellationToken.None, Thresholds, bound: TimeSpan.FromSeconds(seconds: 25)));
        Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 25), actual: bounded.GetElapsedTime(startingTimestamp: 0));
        Assert.Contains(actualString: idle.ToString(), expectedSubstring: "(at most 25 seconds)");
    }
    // The commit condition and the memory and disk conditions hold a step back together, each named with its own need.
    [Fact]
    public void EachShortfallIsNamedWithItsOwnNeedWhenSeveralHoldAStepBack() {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("x", false, false, () => ((calls++ == 0) ? Sample(commit: 82, ram: 1, disk: 2) : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit 82% of limit (admission needs under 75%); freeRAM=1.0GB (admission needs over 5.0GB); freeDisk=2.0GB (admission needs at least 10.0GB).");
    }
    [InlineData(75.0, true)]
    [InlineData(74.9, false)]
    [InlineData(double.NaN, false)]
    [Theory]
    public void CommitHeadroomIsAChargeUnderItsShareOfTheLimitAndAnUnreadableChargeIsNotJudged(double percent, bool held) {
        var reading = (double.IsNaN(d: percent) ? Sample() : Sample(commit: percent));

        Assert.Equal(expected: held, actual: !Thresholds.CommitHasHeadroom(reading: reading));
        Assert.Equal(expected: double.IsNaN(d: percent), actual: double.IsNaN(d: reading.CommitPercent));
        Assert.True(condition: (Thresholds with { CapacityCommitPercent = null }).CommitHasHeadroom(reading: Sample(commit: 99)));
    }
    [Fact]
    public void ACommitChargeOverItsShareAlsoEndsCapacityInTheMonitorAndNamesItInTheReading() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Thresholds);

        Assert.Equal(expected: ["GPU idle cpu=0% freeRAM=8.0GB freeDisk=100.0GB commit=90% reuseNodes=42", "LOADED cpu=0% freeRAM=8.0GB freeDisk=100.0GB commit=90% reuseNodes=42"], actual: monitor.Observe(sample: Sample(commit: 90)));
        Assert.False(condition: monitor.HasCapacity);
        Assert.Equal(expected: ["CAPACITY cpu=0% freeRAM=8.0GB freeDisk=100.0GB commit=60% reuseNodes=42"], actual: monitor.Observe(sample: Sample(commit: 60)));
        Assert.True(condition: monitor.HasCapacity);
    }
    // A verb below the gate passes WorldAdmission before each World start, and the installed admission is HostAdmission: the
    // start waits at a 90% commit charge and goes ahead at 60%, each replica admitted on its own reading, and a bound that
    // passes first refuses the start by exception.
    [Fact]
    public void AVerbsWorldStartWaitsAtNinetyPercentCommitAndRunsAtSixtyThroughTheInstalledAdmission() {
        var clock = new GateClock();
        var readings = 0;
        var asked = new List<(string Step, bool Device, bool Heavy)>();
        using var error = new StringWriter();
        HostAdmit admit = (checkout, step, device, heavySuite, bound, progress, stage, cancellationToken) => {
            asked.Add(item: (step, device, heavySuite));

            return Task.FromResult(result: HostAdmission.Wait(step, device, heavySuite, () => ((readings++ < 2) ? Sample(commit: 90) : Sample(commit: 60)), clock, clock.Advance, error, cancellationToken, Thresholds, bound, stage));
        };

        WorldAdmission.Install(admit: admit);
        try {
            string[] worker = ["Puck.World.dll", "--devices", "none", "--test-batch", "batch.json"];

            // The first replica waits two samples at 90% and starts on the third.
            WorldAdmission.Wait(step: "test worker", worldArguments: worker, heavySuite: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: 3, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit 90% of limit (admission needs under 75%).");
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "capacity returned for test worker");

            // The next replica is admitted on its own reading: 60% starts at once, a windowed boot asks for the GPU.
            WorldAdmission.Wait(step: "test worker", worldArguments: worker, heavySuite: true, cancellationToken: TestContext.Current.CancellationToken);
            WorldAdmission.Wait(step: "sweep", worldArguments: ["Puck.World.dll", "--world", "worlds/debug", "--headless", "false"], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: 5, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Equal(expected: [("test worker", false, true), ("test worker", false, true), ("sweep", true, false)], actual: asked);

            // A machine that never recovers refuses the start at the admission's bound, and the verb's start never happens.
            readings = int.MinValue;
            Assert.Throws<HostAdmissionRefusedException>(testCode: () => WorldAdmission.Wait(step: "canary", device: false, cancellationToken: TestContext.Current.CancellationToken));
        } finally {
            WorldAdmission.Install(admit: null);
        }

        // Without an installed admission a start is admitted at once.
        WorldAdmission.Wait(step: "canary", device: true, cancellationToken: TestContext.Current.CancellationToken);
    }
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--headless" }, false)]
    [InlineData(new[] { "--headless", "true" }, false)]
    [InlineData(new[] { "--headless", "false" }, true)]
    [InlineData(new[] { "--devices", "none", "--test-batch", "batch.json" }, false)]
    [Theory]
    public void AWorldOpensADeviceUnlessItBootsHeadlessOrRunsATestBatch(string[] arguments, bool opens) =>
        Assert.Equal(expected: opens, actual: WorldDevices.OpensDevice(arguments: arguments));
    [Fact]
    public void PersistentPressureStopsAtTheBoundAndCancellationStartsNothing() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: 0), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(HostAdmission.Timeout, clock.GetElapsedTime(startingTimestamp: 0));
        Assert.DoesNotContain("capacity returned", error.ToString());
        Assert.Throws<OperationCanceledException>(testCode: () => HostAdmission.Wait("build", false, false, () => throw new Exception(message: "must not sample"), clock, clock.Advance, error, new CancellationToken(canceled: true), Thresholds));
    }
}
