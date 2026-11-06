using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HostAdmissionLawTests {
    // The 16 GB, six-thread developer machine: capacity above 5 GB free, pressure below 2 GB, disk pressure below 10 GB.
    private static readonly HostLoadThresholds Thresholds = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6);

    // A commit reading is a percentage of a 40 GB commit limit; none is a platform that reports no limit.
    private static HostSample Sample(double cpu = 0, double ram = 8, double disk = 100, string? gpu = null, string? heavy = null, double? commit = null, double limit = 40, HostRunningStart[]? starts = null) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, HeavyTestHolder: heavy, ReuseNodes: 42, CommitChargeGb: ((commit is { } percent) ? (percent * limit / 100.0) : double.NaN), CommitLimitGb: ((commit is null) ? double.NaN : limit), Starts: starts);
    private static bool Admits(HostStartKind kind, HostSample reading, HostStartTracker tracker, GateClock clock, int boundSeconds = 25) {
        using var error = new StringWriter();

        return HostAdmission.Wait("start", (kind == HostStartKind.WindowedWorld), false, () => reading, clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: boundSeconds), null, kind, tracker);
    }

    [Fact]
    public void HostOptionsDefaultToTheAdmissionThresholdsAndStillAllowOverrides() {
        var load = HostCommand.Create().Subcommands.Single(predicate: command => (command.Name == "load"));
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
    // A verb below the gate admits each World start through WorldLaunch, and the installed admission is HostAdmission: the
    // start waits at a 90% commit charge and goes ahead at 60%, each replica admitted on its own reading, and a bound that
    // passes first refuses the start by exception.
    [Fact]
    public void AVerbsWorldStartWaitsAtNinetyPercentCommitAndRunsAtSixtyThroughTheInstalledAdmission() {
        var clock = new GateClock();
        var readings = 0;
        var asked = new List<(string Step, bool Device, bool Heavy, HostStartKind Kind)>();
        using var error = new StringWriter();
        WorldAdmit admit = (checkout, step, device, heavySuite, kind, bound, reserve, progress, stage, cancellationToken) => {
            asked.Add(item: (step, device, heavySuite, kind));

            return Task.FromResult(result: HostAdmission.Wait(step, device, heavySuite, () => ((readings++ < 2) ? Sample(commit: 90, limit: 64) : Sample(commit: 60, limit: 64)), clock, clock.Advance, error, cancellationToken, Thresholds, bound, stage, kind, reserve: reserve));
        };

        WorldAdmission.Install(admit: admit);
        try {
            var worker = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "test worker") { Arguments = ["--test-batch", "batch.json"], HeavySuite = true, Shape = WorldShape.TestBatch };

            // The first replica waits two samples at 90% and starts on the third.
            WorldLaunch.Admit(cancellationToken: TestContext.Current.CancellationToken, request: worker).Dispose();
            Assert.Equal(expected: 3, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit 90% of limit (admission needs under 75%); commit headroom 6.4GB (admission needs at least 9.0GB");
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "capacity returned for test worker");

            // The next replica is admitted on its own reading: 60% starts at once, as does a World booted in process.
            WorldLaunch.Admit(cancellationToken: TestContext.Current.CancellationToken, request: worker).Dispose();
            WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "bench");
            Assert.Equal(expected: 5, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Equal(expected: [("test worker", false, true, HostStartKind.ReproduceReplica), ("test worker", false, true, HostStartKind.ReproduceReplica), ("bench", false, false, HostStartKind.HeadlessWorld)], actual: asked);

            // A machine that never recovers refuses the start at the admission's bound, and the verb's start never happens.
            readings = int.MinValue;
            Assert.Throws<HostAdmissionRefusedException>(testCode: () => WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "canary"));
        } finally {
            WorldAdmission.Install(admit: null);
        }

        // Without an installed admission a start is admitted at once.
        WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "canary");
    }
    // The launch's own view of a World (whether it opens a device, so takes the GPU lease) and the host's reading of the
    // command line it builds agree for every shape, so a running World is classified as the launch admitted it.
    [InlineData(WorldShape.Headless)]
    [InlineData(WorldShape.Windowed)]
    [InlineData(WorldShape.Document)]
    [InlineData(WorldShape.TestBatch)]
    [Theory]
    public void TheLaunchAndTheHostReadAWorldsDeviceAlike(WorldShape shape) {
        var request = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "law") { Arguments = ((shape == WorldShape.TestBatch) ? ["--test-batch", "batch.json"] : []), Shape = shape, World = "worlds/debug" };

        Assert.Equal(expected: request.OpensDevice, actual: WorldDevices.OpensDevice(arguments: request.CommandLine(), start: 1));
        Assert.Equal(expected: request.Kind, actual: WorldDevices.StartKind(arguments: request.CommandLine(), start: 1));
        Assert.Contains(collection: request.CommandLine(), expected: "none");
    }
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--headless" }, false)]
    [InlineData(new[] { "--headless", "true" }, false)]
    [InlineData(new[] { "--headless", "false" }, true)]
    [InlineData(new[] { "--devices", "none", "--test-batch", "batch.json" }, false)]
    [Theory]
    public void AWorldOpensADeviceUnlessItBootsHeadlessOrRunsATestBatch(string[] arguments, bool opens) =>
        Assert.Equal(expected: opens, actual: WorldDevices.OpensDevice(arguments: arguments));
    // A windowed World is expected to peak at 18 GB. At 60% of a 40 GB limit the charge is 24 GB, under the 75% share, but
    // the 16 GB of headroom cannot hold it: the start waits, names the peak, and goes ahead once 20 GB are free.
    [Fact]
    public void AWindowedStartWaitsWhenTheHeadroomIsBelowItsPeakEvenAtSixtyPercentCommit() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("puck_world_start", true, false, () => Sample(commit: 60), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.WindowedWorld, new HostStartTracker()));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 16.0GB (admission needs at least 18.0GB (18.0GB expected peak of a windowed World plus 0.0GB still to grow in 0 admitted start(s)))");
        Assert.DoesNotContain(expectedSubstring: "% of limit", actualString: error.ToString());
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50), tracker: new HostStartTracker(), clock: new GateClock()));
        // The same 60% reading admits a headless World, whose peak is 6 GB.
        Assert.True(condition: Admits(kind: HostStartKind.HeadlessWorld, reading: Sample(commit: 60), tracker: new HostStartTracker(), clock: new GateClock()));
    }
    // A running admitted start counts for what it has left to grow: a World that reached its peak counts for nothing, and
    // one still at 6 GB holds 12 GB more of the headroom.
    [Fact]
    public void ARunningStartCountsOnlyForWhatItHasLeftToGrow() {
        HostRunningStart[] atPeak = [new(Id: 7, Kind: HostStartKind.WindowedWorld, PrivateGb: 18)];
        HostRunningStart[] growing = [new(Id: 7, Kind: HostStartKind.WindowedWorld, PrivateGb: 6)];

        // Commit 50% of 40 GB leaves 20 GB: enough for a windowed World beside one that stopped growing, not beside one still at 6 GB.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50, starts: atPeak), tracker: new HostStartTracker(), clock: new GateClock()));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50, starts: growing), tracker: new HostStartTracker(), clock: new GateClock()));

        var demand = new HostStartTracker().Judge(reading: Sample(commit: 50, starts: growing), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (30.0, 12.0, 1, false), actual: (demand.NeedGb, demand.GrowthGb, demand.Growing, demand.Covered));
        // A process that exited stops counting at once: the next reading no longer lists it.
        var tracker = new HostStartTracker();

        Assert.False(condition: tracker.Judge(reading: Sample(commit: 50, starts: growing), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false).Covered);
        Assert.True(condition: tracker.Judge(reading: Sample(commit: 50), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false).Covered);
    }
    // Reserved growth covers only the starts that have not reported ready: an idle headless World at 0.6 GB that reported
    // steady state counts its usage alone (which the charge already holds), so a windowed start that needs 18 GB of the
    // 22 GB headroom goes ahead beside it; the same World before it reported ready holds its 5.4 GB still to grow and
    // keeps the start out.
    [Fact]
    public void AStartThatReportedReadyReservesNoGrowth() {
        HostRunningStart[] booting = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.6)];
        HostRunningStart[] ready = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.6, Ready: true)];
        // Commit 45% of 40 GB leaves 22 GB.
        var unready = new HostStartTracker().Judge(reading: Sample(commit: 45, starts: booting), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);
        var settled = new HostStartTracker().Judge(reading: Sample(commit: 45, starts: ready), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (5.4, 1, false), actual: (Math.Round(value: unready.GrowthGb, digits: 6), unready.Growing, unready.Covered));
        Assert.Equal(expected: (0.0, 0, true, 18.0), actual: (settled.GrowthGb, settled.Growing, settled.Covered, settled.NeedGb));
    }
    // A World reports steady state in its own words: a headless World once it answers on its console or publishes its
    // attachment, a World that opens a device once its renderer is ready or its bakes settle; boot narration is neither.
    [InlineData("[world.status: tick 3]", true, false, true)]
    [InlineData("[world.control: operator attachment C:/t/puck-control-1.json]", false, false, true)]
    [InlineData("[world.host] physical input devices are off (--devices none); this boot shape opens none either way.", false, false, false)]
    [InlineData("[world.status: tick 3]", true, true, false)]
    [InlineData("[engine: ready at tick 41]", false, true, true)]
    [InlineData("[bakes: settled at tick 90]", false, true, true)]
    [InlineData("[bakes: not settled after 600 seconds, so world.wait released: debug]", false, true, false)]
    [Theory]
    public void AWorldReportsReadyInItsOwnWords(string line, bool stdout, bool device, bool ready) =>
        Assert.Equal(expected: ready, actual: WorldReadiness.Signals(line: line, opensDevice: device, stdout: stdout));
    // Two starts asked in a row cannot take the same headroom: the first admitted start holds its peak until its process
    // shows up, so the second waits, then goes ahead when the process appears (counted at its live size) or the hold passes.
    [Fact]
    public void TwoStartsInARowCannotBothBeAdmittedIntoTheSameHeadroom() {
        var clock = new GateClock();
        var tracker = new HostStartTracker();
        var reading = Sample(commit: 40);

        // 24 GB of headroom holds one windowed World and not two.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock, boundSeconds: 100));
        Assert.True(condition: (clock.GetElapsedTime(startingTimestamp: 0) >= HostStartTracker.ReservationHold));

        // The first start's process appears at 3 GB: its reservation ends and it counts for the 15 GB it has left, not both.
        var answered = new HostStartTracker();
        var asked = Sample(commit: 20, limit: 64);

        Assert.True(condition: answered.Judge(reading: asked, kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: clock, reserve: true).Covered);
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: answered.Judge(reading: asked, kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false).GrowthGb);

        var appeared = answered.Judge(reading: Sample(commit: 20, limit: 64, starts: [new(Id: 9, Kind: HostStartKind.WindowedWorld, PrivateGb: 3)]), kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false);

        Assert.Equal(expected: (15.0, 1), actual: (appeared.GrowthGb, appeared.Growing));
        // A start that nothing else holds back is the only one that reserves: one held by the GPU leaves no reservation.
        var held = new HostStartTracker();
        using var heldError = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("start", true, false, () => Sample(commit: 20, limit: 64, gpu: "Puck.World 9"), clock, clock.Advance, heldError, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 15), null, HostStartKind.WindowedWorld, held));
        Assert.Equal(expected: 0.0, actual: held.Judge(reading: asked, kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false).GrowthGb);
    }
    // A finished start's measured peak raises its kind's expected peak, never past twice the default, and a start
    // still running has not finished.
    [Fact]
    public void AFinishedStartsMeasuredPeakRaisesItsKindsExpectedPeakWithinACeiling() {
        var tracker = new HostStartTracker();
        var clock = new GateClock();

        Assert.Equal(expected: HostLoadThresholds.HeadlessWorldPeakGb, actual: tracker.ExpectedPeakGb(kind: HostStartKind.HeadlessWorld));
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 4, Kind: HostStartKind.HeadlessWorld, PrivateGb: 9)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 4, Kind: HostStartKind.HeadlessWorld, PrivateGb: 4)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: HostLoadThresholds.HeadlessWorldPeakGb, actual: tracker.ExpectedPeakGb(kind: HostStartKind.HeadlessWorld));
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: 9.0, actual: tracker.ExpectedPeakGb(kind: HostStartKind.HeadlessWorld));
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: tracker.ExpectedPeakGb(kind: HostStartKind.WindowedWorld));
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 5, Kind: HostStartKind.HeadlessWorld, PrivateGb: 30)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: (2 * HostLoadThresholds.HeadlessWorldPeakGb), actual: tracker.ExpectedPeakGb(kind: HostStartKind.HeadlessWorld));
    }
    // A process is its id and start ticks: a headless World that reuses a finished heavy suite's id is a new start, so the
    // suite's 15 GB peak is measured for heavy suites when it exits and never carried into the headless kind.
    [Fact]
    public void AReusedProcessIdIsANewStartAndCarriesNoPeakFromTheOneBefore() {
        var tracker = new HostStartTracker();
        var clock = new GateClock();

        _ = tracker.Judge(reading: Sample(starts: [new(Id: 7, Kind: HostStartKind.HeavySuite, PrivateGb: 15, StartTicks: 1)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 7, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.5, StartTicks: 2)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: (15.0, HostLoadThresholds.HeadlessWorldPeakGb), actual: (tracker.ExpectedPeakGb(kind: HostStartKind.HeavySuite), tracker.ExpectedPeakGb(kind: HostStartKind.HeadlessWorld)));
    }
    // A machine whose commit limit sits near one World's peak still admits it when nothing else grows; a platform with no
    // commit reading is never judged.
    [Fact]
    public void APeakNeverExceedsHalfTheLimitAndAnUnreadableCommitIsNotJudged() {
        // A 24 GB limit with 4 GB charged: the 18 GB peak counts as 12 GB, covered by 20 GB of headroom.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: (400.0 / 24.0), limit: 24), tracker: new HostStartTracker(), clock: new GateClock()));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(starts: [new(Id: 1, Kind: HostStartKind.WindowedWorld, PrivateGb: 1)]), tracker: new HostStartTracker(), clock: new GateClock()));
    }
    [InlineData("Puck.World.exe", "Puck.World.exe --devices none --world worlds/debug", HostStartKind.WindowedWorld)]
    [InlineData("dotnet", "dotnet D:\\store\\Puck.World.dll --devices none --headless true", HostStartKind.HeadlessWorld)]
    [InlineData("dotnet", "dotnet D:\\store\\Puck.World.dll --devices none --test-batch b.json", HostStartKind.ReproduceReplica)]
    [InlineData("Puck.World.Tests", "", HostStartKind.HeavySuite)]
    [InlineData("dotnet", "dotnet exec D:\\x\\Puck.World.Presentation.Tests.dll", HostStartKind.HeavySuite)]
    [InlineData("Puck.World", "", HostStartKind.WindowedWorld)]
    [Theory]
    public void TheHostTracksWorldsAndHeavySuitesAsAdmittedStarts(string name, string commandLine, HostStartKind kind) =>
        Assert.Equal(expected: kind, actual: HostProcesses.RunningStart(name: name, commandLine: commandLine));
    [InlineData("pwsh", "pwsh -c Puck.World.dll")]
    [InlineData("dotnet", "dotnet build src/Puck.World/Puck.World.csproj -c Release")]
    [InlineData("MSBuild", "MSBuild.dll /nodemode:1")]
    [InlineData("dotnet", "dotnet D:\\x\\Puck.Cli.dll lease heavy")]
    [Theory]
    public void ShellsBuildsAndOtherProcessesAreNotTrackedStarts(string name, string commandLine) =>
        Assert.Null(@object: HostProcesses.RunningStart(name: name, commandLine: commandLine));
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
