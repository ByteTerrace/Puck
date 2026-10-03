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
    public void PressureWinsOverCapacityAndPrintsOncePerChangeOfItsReasons() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);

        // Low CPU and plenty of memory would be CAPACITY, but the disk is under its pressure threshold.
        Assert.Equal(actual: monitor.Observe(sample: Reading(disk: 9.5, seconds: 0)), expected: ["GPU idle cpu=10% freeRAM=8.0GB freeDisk=9.5GB reuseNodes=0", "PRESSURE freeDisk<10.0GB cpu=10% freeRAM=8.0GB freeDisk=9.5GB reuseNodes=0"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(disk: 9.4, seconds: 3600)));
        Assert.Equal(actual: monitor.Observe(sample: Reading(disk: 9.5, ram: 1.5, seconds: 3610)), expected: ["PRESSURE freeRAM<2.0GB,freeDisk<10.0GB cpu=10% freeRAM=1.5GB freeDisk=9.5GB reuseNodes=0"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(disk: 9.5, ram: 1.5, seconds: 7200)));
    }
    [Fact]
    public void CapacityWaitsForAFullWindowAndPrintsOnceWhileItHolds() {
        var monitor = new HostLoadMonitor(cpuSamples: 3, thresholds: Laptop);

        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0))), expected: ["GPU idle"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 10)));
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 20))), expected: ["CAPACITY"]);
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 620)));
        Assert.Empty(collection: monitor.Observe(sample: Reading(seconds: 7200)));
    }
    [Fact]
    public void EachAdmissionTransitionPrintsExactlyOneLine() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);
        (double Cpu, double Ram, string? Gpu)[] readings = [
            (10, 8, null), (10, 8, null),
            (90, 8, null), (90, 8, null),
            (90, 1.5, null), (10, 1.5, null),
            (10, 1.5, "Puck.Vulkan.Tests 77"), (10, 8, "Puck.Vulkan.Tests 77"),
            (10, 8, null), (10, 8, null),
        ];
        var lines = readings.SelectMany(selector: (reading, index) => monitor.Observe(sample: Reading(cpu: reading.Cpu, gpu: reading.Gpu, ram: reading.Ram, seconds: (index * 10)))).ToArray();

        // Capacity, its end, pressure, the GPU taken and released, and capacity again: one line each, no repeat.
        Assert.Equal(expected: ["GPU idle", "CAPACITY", "LOADED", "PRESSURE", "GPU busy", "CAPACITY", "GPU idle"], actual: Kinds(lines: lines));
    }
    [Fact]
    public void CapacityJudgesTheCpuMeanOverTheWindowNotTheLastReading() {
        var monitor = new HostLoadMonitor(cpuSamples: 3, thresholds: Laptop);

        _ = monitor.Observe(sample: Reading(cpu: 90, seconds: 0));
        _ = monitor.Observe(sample: Reading(cpu: 90, seconds: 10));

        // The last reading is idle, but the mean of 90, 90 and 0 is 60, over the 50% capacity threshold.
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(cpu: 0, seconds: 20))), expected: ["LOADED"]);

        // Now the window holds 90, 0 and 0: a mean of 30.
        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(cpu: 0, seconds: 30))), expected: ["CAPACITY"]);
    }
    [Fact]
    public void CapacityNeedsBothItsCpuAndItsMemoryThreshold() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop with { CapacityRamGb = null });

        Assert.Equal(actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0))), expected: ["GPU idle"]);

        var full = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);

        // Free memory not over the capacity threshold holds CAPACITY back without being PRESSURE.
        Assert.Equal(actual: Kinds(lines: full.Observe(sample: Reading(ram: 4, seconds: 0))), expected: ["GPU idle", "LOADED"]);
    }
    [InlineData("Puck.World", @"C:\Puck\src\Puck.World\bin\Release\net10.0\Puck.World.exe --world worlds/a.puck")]
    [InlineData("dotnet", @"dotnet C:\Users\a\AppData\Local\Puck\world-builds\k\Puck.World.dll --headless true")]
    [InlineData("dotnet", @"dotnet ""C:\scratch\l1-cli\Puck.Cli.dll"" canary pipeline-ink")]
    [InlineData("dotnet", "dotnet /tmp/cli/Puck.Cli.dll parity")]
    [InlineData("puck", "puck counters --check")]
    [InlineData("Puck.World.Tests", @"C:\Puck\tests\Puck.World.Tests\bin\Release\net10.0\Puck.World.Tests.exe --port 1")]
    [InlineData("Puck.DirectX.Tests", "Puck.DirectX.Tests.exe")]
    [InlineData("Puck.Platform.Windows.Tests", "Puck.Platform.Windows.Tests.exe")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.Vulkan.Tests\bin\Release\net10.0\Puck.Vulkan.Tests.dll")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.DirectX.Tests\bin\Release\net10.0\Puck.DirectX.Tests.dll")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.World.Tests\bin\Release\net10.0\Puck.World.Tests.dll")]
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
    [InlineData("dotnet", "dotnet run --project src/Puck.World")]
    [InlineData("dotnet", "dotnet test tests/Puck.Cli.Tests --logger Puck.World.dll")]
    [InlineData("dotnet", "dotnet tool run puck canary x")]
    [InlineData("tool", "tool /tmp/puck canary")]
    [InlineData("dotnet", "dotnet /tmp/tool.dll --input /tmp/Puck.World.dll")]
    [InlineData("dotnet", "dotnet /tmp/tool.dll --input /tmp/Puck.Cli.dll canary")]
    [InlineData("puck", "puck canary --list")]
    [InlineData("puck", "puck canary --plan --merge")]
    [InlineData("puck", "puck parity compare left right --contract contract.json")]
    [InlineData("puck", "puck counters compare left right")]
    [InlineData("puck", "puck counters --help")]
    [Theory]
    public void OnlyTheRunningEntryPointCanClaimGpuWork(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [InlineData("Puck.Cli", "\"C:/a path/Puck.Cli.exe\" parity")]
    [InlineData("dotnet", "dotnet exec --runtimeconfig runtime.json \"C:/a path/Puck.World.dll\" --world build")]
    [InlineData("dotnet", "dotnet exec /tmp/Puck.Vulkan.Tests.dll")]
    [InlineData("Puck.World.Test", "/tmp/Puck.World.Tests --port 1")]
    [InlineData("Puck.World", "Puck.World --world build")]
    [InlineData("puck", "puck canary --list false --plan false --merge")]
    [InlineData("puck", "puck counters --world compare --script script")]
    [InlineData("dotnet", "dotnet\0/tmp/a path/Puck.World.dll\0--world\0build\0")]
    [Theory]
    public void ApphostsAndManagedEntryPointsAreGpuWorkRegardlessOfArgumentWords(string name, string commandLine) =>
        Assert.True(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [InlineData("dotnet", "dotnet tool.dll --text nodeReuse:true")]
    [InlineData("dotnet", "dotnet MSBuild.dll /nodeReuse:true")]
    [Theory]
    public void ReuseRequiresAnActualMsbuildWorker(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsReuseNode(commandLine: commandLine, name: name));
    [InlineData("--capacity-cpu", "NaN")]
    [InlineData("--capacity-cpu", "Infinity")]
    [InlineData("--capacity-cpu", "-1")]
    [InlineData("--capacity-cpu", "101")]
    [InlineData("--capacity-ram", "-1")]
    [InlineData("--pressure-ram", "NaN")]
    [InlineData("--pressure-disk", "-1")]
    [Theory]
    public void ThresholdsRefuseNonphysicalValuesBeforeReadingTheMachine(string option, string value) =>
        Assert.NotEmpty(collection: HostCommand.Create().Parse(args: ["load", option, value]).Errors);
    [InlineData(100UL, 1000UL, 99UL, 1010UL)]
    [InlineData(100UL, 1000UL, 120UL, 1010UL)]
    [Theory]
    public void InconsistentCpuCountersCannotAdmitWork(ulong idleBefore, ulong totalBefore, ulong idleAfter, ulong totalAfter) {
        var cpu = HostProbe.CpuPercent(after: (idleAfter, totalAfter), before: (idleBefore, totalBefore));
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Laptop);

        Assert.True(condition: double.IsNaN(d: cpu));
        Assert.Equal(expected: ["GPU idle", "LOADED"], actual: Kinds(lines: monitor.Observe(sample: Reading(seconds: 0, cpu: cpu))));
    }
    [InlineData("/mnt/checkout/worlds")]
    [InlineData("//server/share/checkout")]
    [Theory]
    public void DiskSpaceIsQueriedAtTheWorkingDirectoryIncludingMountsAndShares(string directory) {
        var free = HostProbe.FreeDiskGb(directory: directory, availableBytes: path => {
            Assert.Equal(actual: path, expected: directory);
            return (3UL * 1073741824);
        });

        Assert.Equal(actual: free, expected: 3);
    }
}
