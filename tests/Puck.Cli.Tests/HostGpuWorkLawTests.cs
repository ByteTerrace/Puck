using Puck.Cli.Gate;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="HostProcesses"/> classifies a running process as GPU work: a World that opens a
/// device, or a device-law test host whose arguments can select a device law, and never a CLI verb (the World it starts
/// holds the device, under the GPU lease its launch takes), a build, a shell or a CPU selection. Every law feeds injected
/// command lines; none reads the machine.</summary>
public sealed class HostGpuWorkLawTests {

    [InlineData("Puck.World", @"C:\Puck\src\Puck.World\bin\Release\net11.0\Puck.World.exe --world worlds/a.puck")]
    [InlineData("dotnet", @"dotnet C:\Users\a\AppData\Local\Puck\world-builds\k\Puck.World.dll --world w --headless false --devices none")]
    [InlineData("Puck.World.Tests", @"C:\Puck\tests\Puck.World.Tests\bin\Release\net11.0\Puck.World.Tests.exe --port 1")]
    [InlineData("Puck.DirectX.Tests", "Puck.DirectX.Tests.exe")]
    [InlineData("Puck.Platform.Windows.Tests", "Puck.Platform.Windows.Tests.exe")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.Vulkan.Tests\bin\Release\net11.0\Puck.Vulkan.Tests.dll")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.DirectX.Tests\bin\Release\net11.0\Puck.DirectX.Tests.dll")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.World.Tests\bin\Release\net11.0\Puck.World.Tests.dll")]
    [Theory]
    public void TheWorldAndADeviceTestHostAreGpuWork(string name, string commandLine) =>
        Assert.True(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    // A headless World opens no device, so it never holds a device step back.
    [InlineData("dotnet", @"dotnet C:\Users\a\AppData\Local\Puck\world-builds\k\Puck.World.dll --headless true")]
    [InlineData("dotnet", "dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug --devices none --headless --exit-after-seconds 20")]
    [InlineData("Puck.World", "Puck.World.exe --world worlds/debug --headless")]
    [InlineData("dotnet", "dotnet build tests/Puck.DirectX.Tests -c Release -m:2 -nodeReuse:false")]
    [InlineData("dotnet", "dotnet build src/Puck.World -c Release")]
    [InlineData("dotnet", "dotnet restore src/Puck.World")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\Program Files\dotnet\sdk\10.0.402\MSBuild.dll"" /nologo /nodemode:1 /nodeReuse:true")]
    [InlineData("MSBuild", "MSBuild.exe /nodemode:1")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" exec ""C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll"" /noconfig /out:obj\Release\net11.0\Puck.World.dll")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\VBCSCompiler.dll"" -pipename:x")]
    [InlineData("VBCSCompiler", "VBCSCompiler.exe -pipename:x")]
    [InlineData("pwsh", "pwsh -c Get-Process Puck.World; puck canary x")]
    [InlineData("bash", "bash -c 'grep -E \"Puck.Cli.dll canary\"'")]
    // A filter or follower whose arguments name a test assembly runs no test: a run is the process executing it.
    [InlineData("grep", "grep.exe --line-buffered -E \"passed|Puck.World.Tests exit\"")]
    [InlineData("tail", @"tail -f C:\scratch\Puck.World.Tests\gate.steps")]
    [InlineData("node", "node watch.js tests/Puck.World.Tests/bin/Release/net11.0/Puck.World.Tests.dll")]
    [InlineData("dotnet", "dotnet tool run report --assembly Puck.World.Tests.dll")]
    [InlineData("dotnet", "dotnet /tmp/cli/Puck.Cli.dll host load --watch")]
    [InlineData("Puck.Cli.Tests", @"C:\Puck\tests\Puck.Cli.Tests\bin\Release\net11.0\Puck.Cli.Tests.exe -class Puck.Cli.Tests.CanaryPlanLawTests")]
    [InlineData("testhost", @"testhost.exe C:\Puck\tests\Puck.Cli.Tests\bin\Release\net11.0\Puck.Cli.Tests.dll")]
    [Theory]
    public void BuildsShellsAndOtherTestsAreNeverGpuWork(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [InlineData("Puck.World.Tests", @"C:\Puck\tests\Puck.World.Tests\bin\Release\net11.0\Puck.World.Tests.exe --filter-not-trait Category=Gpu")]
    [InlineData("dotnet", "dotnet exec tests/Puck.World.Tests/bin/Release/net11.0/Puck.World.Tests.dll --filter-class *CaptureLawTests --filter-not-trait Category=Gpu")]
    [InlineData("Puck.Vulkan.Tests", "Puck.Vulkan.Tests.exe --filter-not-trait=Category=Gpu")]
    [InlineData("dotnet", "dotnet\0/tmp/a path/Puck.DirectX.Tests.dll\0--filter-not-trait\0Category=Gpu\0")]
    [Theory]
    public void ADeviceTestRunCarryingTheCpuSelectionIsNotGpuWork(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [InlineData("Puck.World.Tests", "Puck.World.Tests.exe --filter-trait Category=Gpu")]
    [InlineData("Puck.World.Tests", "Puck.World.Tests.exe --filter-class *WorldCaptureHoldLawTests")]
    [InlineData("dotnet", "dotnet exec Puck.World.Tests.dll --filter-not-trait Category=Slow")]
    [InlineData("Puck.World.Tests", "Puck.World.Tests.exe --filter-not-trait")]
    [InlineData("Puck.World.Tests", "Puck.World.Tests.exe @run.rsp --filter-not-trait Category=Gpu")]
    [Theory]
    public void ADeviceTestRunThatCanSelectADeviceLawIsGpuWork(string name, string commandLine) =>
        Assert.True(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    [Fact]
    public void EveryDeviceSuiteIsGpuWorkUnderItsGateSelectionAndNotUnderTheCpuSelection() {
        foreach (var (suite, selection) in GatePlan.DeviceSuites) {
            foreach (var (name, prefix) in new[] { (suite, $"{suite}.exe"), ("dotnet", $"dotnet exec {suite}.dll") }) {
                Assert.True(condition: HostProcesses.IsGpuWork(commandLine: $"{prefix} {string.Join(separator: ' ', value: selection)}", name: name));
                Assert.False(condition: HostProcesses.IsGpuWork(commandLine: $"{prefix} {string.Join(separator: ' ', value: CliTestRun.CpuSelection)}", name: name));
            }
        }
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
    [InlineData("dotnet", "dotnet exec --runtimeconfig runtime.json \"C:/a path/Puck.World.dll\" --world build")]
    [InlineData("dotnet", "dotnet exec /tmp/Puck.Vulkan.Tests.dll")]
    [InlineData("Puck.World.Test", "/tmp/Puck.World.Tests --port 1")]
    [InlineData("Puck.World", "Puck.World --world build")]
    [InlineData("dotnet", "dotnet\0/tmp/a path/Puck.World.dll\0--world\0build\0")]
    [Theory]
    public void ApphostsAndManagedEntryPointsAreGpuWorkRegardlessOfArgumentWords(string name, string commandLine) =>
        Assert.True(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
    // A verb that starts a World is not GPU work while it waits for the GPU lease: two such verbs queue on the lease rather
    // than each reading the other as the GPU's holder.
    [InlineData("dotnet", @"dotnet ""C:\scratch\l1-cli\Puck.Cli.dll"" canary pipeline-ink")]
    [InlineData("dotnet", "dotnet /tmp/cli/Puck.Cli.dll parity")]
    [InlineData("puck", "puck counters --world w.json")]
    [InlineData("Puck.Cli", "\"C:/a path/Puck.Cli.exe\" parity")]
    [InlineData("puck", "puck motion --backend vulkan")]
    [Theory]
    public void AVerbThatStartsAWorldIsNotGpuWorkItself(string name, string commandLine) =>
        Assert.False(condition: HostProcesses.IsGpuWork(commandLine: commandLine, name: name));
}
