using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HostAdmissionLawTests {
    private static HostSample Sample(double cpu = 0, double ram = 8, double disk = 100, string? gpu = null, string? heavy = null) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, HeavyTestHolder: heavy, ReuseNodes: 42);

    [Fact]
    public void HostOptionsDefaultToTheAdmissionThresholdsAndStillAllowOverrides() {
        var load = HostCommand.Create(composition: SuiteRoot.Composition).Subcommands.Single(predicate: command => (command.Name == "load"));
        var defaults = HostLoadThresholds.Default;
        (string Name, double? Value)[] thresholds = [("--capacity-cpu", defaults.CapacityCpuPercent), ("--capacity-ram", defaults.CapacityRamGb), ("--pressure-ram", defaults.PressureRamGb), ("--pressure-disk", defaults.PressureDiskGb)];

        foreach (var (name, value) in thresholds) {
            var option = ((Option<double?>)load.Options.Single(predicate: option => (option.Name == name)));

            Assert.Equal(value, load.Parse([]).GetValue(option: option));
            Assert.Equal(1, load.Parse([name, "1"]).GetValue(option: option));
        }
        Assert.Contains("Defaults:", CliHelp.DetailOf(command: load));
    }
    [Fact]
    public void IdleAdmissionHasNoWaitOrRefusalEvenWithReuseNodes() {
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(), new GateClock(), _ => Assert.Fail(message: "idle must not wait"), error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("step", true, false, () => ((++calls <= 2) ? busy : Sample()), clock, clock.Advance, error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => readings[calls++], clock, clock.Advance, error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("baselines corpus-inventory", false, false, () => Sample(gpu: "Puck.World.Tests 7"), clock, _ => Assert.Fail(message: "a CPU step must not wait on the GPU"), error, CancellationToken.None));
        Assert.Equal(expected: "", actual: error.ToString());

        var calls = 0;

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => ((calls++ == 0) ? Sample(gpu: "Puck.World 9") : Sample()), clock, clock.Advance, error, CancellationToken.None));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "the GPU is held by Puck.World 9");

        // A CPU step still waits for memory headroom, and names it rather than a GPU holder.
        using var busy = new StringWriter();

        calls = 0;
        Assert.True(condition: HostAdmission.Wait("affected", false, false, () => ((calls++ == 0) ? Sample(ram: 1, gpu: "Puck.World 9") : Sample(gpu: "Puck.World 9")), clock, clock.Advance, busy, CancellationToken.None));
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
        Assert.True(condition: HostAdmission.Wait("build", device, false, () => Sample(cpu: 99), clock, _ => Assert.Fail(message: "CPU load must not hold a step back"), error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", false, true, Reading, clock, clock.Advance, error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("baselines state", false, false, () => Sample(heavy: "Puck.World.Tests 11"), clock, _ => Assert.Fail(message: "a light step must not wait on a heavy run"), light, CancellationToken.None));
        Assert.Equal(expected: "", actual: light.ToString());
    }
    [Fact]
    public void AHeavySuiteIsRefusedOnlyAtItsTwoHourBound() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("Puck.World.Tests", false, true, () => Sample(heavy: "Puck.World.Tests 11"), clock, clock.Advance, error, CancellationToken.None));
        Assert.Equal(expected: TimeSpan.FromHours(hours: 2), actual: clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Equal(expected: HostAdmission.HeavyTimeout, actual: TimeSpan.FromHours(hours: 2));
    }

    private static string HostAdmissionReason(double ram) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        // Too little memory first, then headroom.
        _ = HostAdmission.Wait("x", true, false, () => ((calls++ == 0) ? Sample(ram: ram) : Sample()), clock, clock.Advance, error, CancellationToken.None);
        return error.ToString();
    }

    [Fact]
    public void PersistentPressureStopsAtTheBoundAndCancellationStartsNothing() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: 0), clock, clock.Advance, error, CancellationToken.None));
        Assert.Equal(HostAdmission.Timeout, clock.GetElapsedTime(startingTimestamp: 0));
        Assert.DoesNotContain("capacity returned", error.ToString());
        Assert.Throws<OperationCanceledException>(testCode: () => HostAdmission.Wait("build", false, false, () => throw new Exception(message: "must not sample"), clock, clock.Advance, error, new CancellationToken(canceled: true)));
    }
}
