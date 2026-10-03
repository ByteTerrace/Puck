using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class HostAdmissionLawTests {
    private static HostSample Sample(double cpu = 0, double ram = 8, double disk = 100, string? gpu = null) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, ReuseNodes: 42);

    [Fact]
    public void HostOptionsDefaultToTheAdmissionThresholdsAndStillAllowOverrides() {
        var load = HostCommand.Create().Subcommands.Single(predicate: command => (command.Name == "load"));
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

        Assert.True(condition: HostAdmission.Wait("build", false, () => Sample(), new GateClock(), _ => Assert.Fail(message: "idle must not wait"), error, CancellationToken.None));
        Assert.Equal("", error.ToString());
    }
    [InlineData("cpu")]
    [InlineData("ram")]
    [InlineData("disk")]
    [InlineData("gpu")]
    [Theory]
    public void CapacityWaitsVisiblyThenReturnsRegardlessOfLineThrottling(string pressure) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();
        var busy = pressure switch {
            "cpu" => Sample(cpu: 99),
            "ram" => Sample(ram: 1),
            "disk" => Sample(disk: 1),
            _ => Sample(gpu: "Puck.World 42"),
        };

        Assert.True(condition: HostAdmission.Wait("step", true, () => ((++calls <= 2) ? busy : Sample()), clock, clock.Advance, error, CancellationToken.None));
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
        HostSample[] readings = [Sample(gpu: "testhost 77"), Sample(gpu: "testhost 77"), Sample(gpu: "Puck.World 9"), Sample(cpu: 99), Sample()];

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, () => readings[calls++], clock, clock.Advance, error, CancellationToken.None));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

        Assert.Equal(expected: 3, actual: lines.Length);
        Assert.EndsWith(actualString: lines[0].TrimEnd(), expectedEndString: "the GPU is held by testhost 77.");
        Assert.EndsWith(actualString: lines[1].TrimEnd(), expectedEndString: "the GPU is held by Puck.World 9.");
        Assert.Contains(actualString: lines[2], expectedSubstring: "capacity returned for Puck.World.Tests");
        Assert.Contains(actualString: HostAdmissionReason(cpu: 99), expectedSubstring: "no capacity: cpu=99%");
    }
    [Fact]
    public void OnlyADeviceStepWaitsForTheGpuHolder() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("baselines corpus-inventory", false, () => Sample(gpu: "Puck.World.Tests 7"), clock, _ => Assert.Fail(message: "a CPU step must not wait on the GPU"), error, CancellationToken.None));
        Assert.Equal(expected: "", actual: error.ToString());

        var calls = 0;

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, () => ((calls++ == 0) ? Sample(gpu: "Puck.World 9") : Sample()), clock, clock.Advance, error, CancellationToken.None));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "the GPU is held by Puck.World 9");

        // A CPU step still waits for CPU and memory capacity, and names it rather than a GPU holder.
        using var busy = new StringWriter();

        calls = 0;
        Assert.True(condition: HostAdmission.Wait("affected", false, () => ((calls++ == 0) ? Sample(cpu: 99, gpu: "Puck.World 9") : Sample(gpu: "Puck.World 9")), clock, clock.Advance, busy, CancellationToken.None));
        Assert.Contains(actualString: busy.ToString(), expectedSubstring: "no capacity: cpu=99%");
        Assert.DoesNotContain(expectedSubstring: "GPU", actualString: busy.ToString());
    }

    private static string HostAdmissionReason(double cpu) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        // A busy CPU first, then capacity.
        _ = HostAdmission.Wait("x", true, () => ((calls++ == 0) ? Sample(cpu: cpu) : Sample()), clock, clock.Advance, error, CancellationToken.None);
        return error.ToString();
    }

    [Fact]
    public void PersistentPressureStopsAtTheBoundAndCancellationStartsNothing() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("build", false, () => Sample(ram: 0), clock, clock.Advance, error, CancellationToken.None));
        Assert.Equal(HostAdmission.Timeout, clock.GetElapsedTime(startingTimestamp: 0));
        Assert.DoesNotContain("capacity returned", error.ToString());
        Assert.Throws<OperationCanceledException>(testCode: () => HostAdmission.Wait("build", false, () => throw new Exception(message: "must not sample"), clock, clock.Advance, error, new CancellationToken(canceled: true)));
    }
}
