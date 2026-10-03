using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="HostLoadMonitor"/> turns readings into GPU, PRESSURE and CAPACITY lines by
/// its rules, and <see cref="HostProcesses"/> classifies processes as GPU work or MSBuild reuse nodes. Every law feeds
/// injected readings and command lines; none reads the machine.</summary>
public sealed class HostLoadLawTests {
    private static readonly DateTimeOffset Start = new(day: 1, hour: 0, minute: 0, month: 1, offset: TimeSpan.Zero, second: 0, year: 2000);
    private static readonly HostLoadThresholds Laptop = new(CapacityCpuPercent: 50, CapacityRamGb: 5, PressureDiskGb: 10, PressureRamGb: 2);

    private static HostSample Reading(int seconds, double cpu = 10, double ram = 8, double disk = 50, string? gpu = null, int reuse = 0) =>
        new(At: Start.AddSeconds(seconds: seconds), CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, ReuseNodes: reuse);
    private static string[] Kinds(IReadOnlyList<string> lines) => [.. lines.Select(selector: static line => (line.Split(separator: ' ')[0] + ((line.StartsWith(comparisonType: StringComparison.Ordinal, value: "GPU ")) ? $" {line.Split(separator: ' ')[1]}" : string.Empty)))];

    [Fact]
    public void TheGpuStateIsReportedOnceAtTheStartAndOnEveryChangeAfter() {
        var monitor = new HostLoadMonitor(cpuSamples: 6, thresholds: new HostLoadThresholds(CapacityCpuPercent: null, CapacityRamGb: null, PressureDiskGb: null, PressureRamGb: null));

        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0))), expected: ["GPU idle"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 10)));
        Assert.Equal(actual: monitor.Observe(sample: Reading(gpu: "Puck.World 4242", seconds: 20)), expected: ["GPU busy (Puck.World 4242) cpu=10% freeRAM=8.0GB freeDisk=50.0GB reuseNodes=0"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(gpu: "Puck.World 4242", seconds: 30)));
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 40))), expected: ["GPU idle"]);
    }
    [Fact]
    public void WithoutThresholdsNoReadingIsJudged() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: new HostLoadThresholds(CapacityCpuPercent: null, CapacityRamGb: null, PressureDiskGb: null, PressureRamGb: null));

        _ = monitor.Observe(sample: Reading(seconds: 0));

        Assert.Empty(collection: monitor.Observe(sample: Reading(cpu: 0, disk: 0.1, ram: 0.1, seconds: 3600)));
    }
    [Fact]
    public void PressureWinsOverCapacityAndRepeatsAtMostEveryFiveMinutes() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);

        // Low CPU and plenty of memory would be CAPACITY, but the disk is under its pressure threshold.
        Assert.Equal(actual: monitor.Observe(sample: Reading(disk: 9.5, seconds: 0)), expected: ["GPU idle cpu=10% freeRAM=8.0GB freeDisk=9.5GB reuseNodes=0", "PRESSURE freeDisk<10.0GB cpu=10% freeRAM=8.0GB freeDisk=9.5GB reuseNodes=0"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(disk: 9.5, ram: 1.5, seconds: 299)));
        Assert.Equal(actual: monitor.Observe(sample: Reading(disk: 9.5, ram: 1.5, seconds: 300)), expected: ["PRESSURE freeRAM<2.0GB,freeDisk<10.0GB cpu=10% freeRAM=1.5GB freeDisk=9.5GB reuseNodes=0"]);
    }
    [Fact]
    public void CapacityWaitsForAFullWindowAndRepeatsAtMostEveryTenMinutes() {
        var monitor = new HostLoadMonitor(cpuSamples: 3, thresholds: Laptop);

        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0))), expected: ["GPU idle"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 10)));
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 20))), expected: ["CAPACITY"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 619)));
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 620))), expected: ["CAPACITY"]);
    }
    [Fact]
    public void CapacityJudgesTheCpuMeanOverTheWindowNotTheLastReading() {
        var monitor = new HostLoadMonitor(cpuSamples: 3, thresholds: Laptop);

        _ = monitor.Observe(sample: Reading(cpu: 90, seconds: 0));
        _ = monitor.Observe(sample: Reading(cpu: 90, seconds: 10));

        // The last reading is idle, but the mean of 90, 90 and 0 is 60, over the 50% capacity threshold.
        Assert.Empty(collection: monitor.Observe(sample: Reading(cpu: 0, seconds: 20)));

        // Now the window holds 90, 0 and 0: a mean of 30.
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(cpu: 0, seconds: 30))), expected: ["CAPACITY"]);
    }
    [Fact]
    public void CapacityNeedsBothItsCpuAndItsMemoryThreshold() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop with { CapacityRamGb = null });

        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0))), expected: ["GPU idle"]);

        var full = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);

        // Free memory not over the capacity threshold holds CAPACITY back without being PRESSURE.
        Assert.Equal(actual: Kinds(lines: full.Observe(sample: Reading(ram: 4, seconds: 0))), expected: ["GPU idle"]);
    }
    [InlineData("Puck.World", @"C:\Puck\src\Puck.World\bin\Release\net10.0\Puck.World.exe --world worlds/a.puck")]
    [InlineData("dotnet", @"dotnet C:\Users\a\AppData\Local\Puck\world-builds\k\Puck.World.dll --headless true")]
    [InlineData("dotnet", @"dotnet ""C:\scratch\l1-cli\Puck.Cli.dll"" canary pipeline-ink")]
    [InlineData("dotnet", "dotnet /tmp/cli/Puck.Cli.dll parity")]
    [InlineData("puck", "puck counters --check")]
    [InlineData("Puck.World.Tests", @"C:\Puck\tests\Puck.World.Tests\bin\Release\net10.0\Puck.World.Tests.exe --port 1")]
    [InlineData("Puck.DirectX.Tests", "Puck.DirectX.Tests.exe")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.Vulkan.Tests\bin\Release\net10.0\Puck.Vulkan.Tests.dll")]
    [Theory]
    public void TheWorldAGpuVerbAndADeviceTestHostAreGpuWork(string name, string commandLine) =>
        Assert.True(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [InlineData("dotnet", "dotnet build tests/Puck.DirectX.Tests -c Release -m:2 -nodeReuse:false")]
    [InlineData("dotnet", "dotnet build src/Puck.World -c Release")]
    [InlineData("dotnet", "dotnet restore src/Puck.World")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\Program Files\dotnet\sdk\10.0.401\MSBuild.dll"" /nologo /nodemode:1 /nodeReuse:true")]
    [InlineData("MSBuild", "MSBuild.exe /nodemode:1")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" exec ""C:\Program Files\dotnet\sdk\10.0.401\Roslyn\bincore\csc.dll"" /noconfig /out:obj\Release\net10.0\Puck.World.dll")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\Program Files\dotnet\sdk\10.0.401\Roslyn\bincore\VBCSCompiler.dll"" -pipename:x")]
    [InlineData("VBCSCompiler", "VBCSCompiler.exe -pipename:x")]
    [InlineData("pwsh", "pwsh -c Get-Process Puck.World; puck canary x")]
    [InlineData("bash", "bash -c 'grep -E \"Puck.Cli.dll canary\"'")]
    [InlineData("dotnet", "dotnet /tmp/cli/Puck.Cli.dll host load --watch")]
    [InlineData("Puck.Cli.Tests", @"C:\Puck\tests\Puck.Cli.Tests\bin\Release\net10.0\Puck.Cli.Tests.exe -class Puck.Cli.Tests.CanaryPlanLawTests")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.Cli.Tests\bin\Release\net10.0\Puck.Cli.Tests.dll")]
    [Theory]
    public void BuildsShellsAndOtherTestsAreNeverGpuWork(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [Fact]
    public void AReuseNodeIsAnMsbuildNodeStartedForReuse() {
        Assert.True(condition: HostProcesses.IsReuseNode(commandLine: @"dotnet ""C:\Program Files\dotnet\sdk\10.0.401\MSBuild.dll"" /nodemode:1 /nodeReuse:true", name: "dotnet"));
        Assert.False(condition: HostProcesses.IsReuseNode(commandLine: @"dotnet ""C:\Program Files\dotnet\sdk\10.0.401\MSBuild.dll"" /nodemode:1 /nodeReuse:false", name: "dotnet"));
        Assert.False(condition: HostProcesses.IsReuseNode(commandLine: "pwsh -c dotnet build -nodeReuse:true", name: "pwsh"));
    }
}
