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

        Assert.True(condition: HostAdmission.Wait("build", () => Sample(), new GateClock(), _ => Assert.Fail(message: "idle must not wait"), error, CancellationToken.None));
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

        Assert.True(condition: HostAdmission.Wait("step", () => ((++calls <= 2) ? busy : Sample()), clock, clock.Advance, error, CancellationToken.None));
        Assert.Equal(actual: calls, expected: 3);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 20), clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Contains("waiting for host capacity before step", error.ToString());
        Assert.Contains("capacity returned for step", error.ToString());
        Assert.Equal(2, error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Length);
    }
    [Fact]
    public void PersistentPressureStopsAtTheBoundAndCancellationStartsNothing() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("build", () => Sample(ram: 0), clock, clock.Advance, error, CancellationToken.None));
        Assert.Equal(HostAdmission.Timeout, clock.GetElapsedTime(startingTimestamp: 0));
        Assert.DoesNotContain("capacity returned", error.ToString());
        Assert.Throws<OperationCanceledException>(testCode: () => HostAdmission.Wait("build", () => throw new Exception(message: "must not sample"), clock, clock.Advance, error, new CancellationToken(canceled: true)));
    }
}
