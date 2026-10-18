using Puck.Abstractions.Gpu;
using Puck.Commands;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for the operator's <c>gpu.faults</c> verb (<see cref="GpuFaultsCommandModule"/>): each form arms the
/// host's <see cref="GpuCreationFaults"/> as it says and answers with what is armed, a removal reading <c>remove:</c>
/// where a loss reads <c>lose:</c>, and a malformed form answers its usage.</summary>
public sealed class GpuFaultsCommandModuleLawTests {
    private static (CommandRegistry Registry, GpuCreationFaults Faults) Console() {
        var faults = new GpuCreationFaults();

        return (new CommandRegistry(modules: [new GpuFaultsCommandModule(faults: faults)]), faults);
    }

    [Fact]
    public void EachFormArmsWhatItSaysAndAnswersWithIt() {
        var (registry, faults) = Console();

        Assert.StartsWith(expectedStartString: "[gpu.faults: armed=pipeline:2 ", actualString: registry.Submit(line: "gpu.faults arm pipeline 2").Output);
        Assert.StartsWith(expectedStartString: "[gpu.faults: armed=pipeline:2,lose:3 ", actualString: registry.Submit(line: "gpu.faults lose 3").Output);
        Assert.StartsWith(expectedStartString: "[gpu.faults: armed=pipeline:2,remove:1 ", actualString: registry.Submit(line: "gpu.faults remove").Output);
        Assert.True(condition: faults.TryGetArmedLoss(remaining: out _, removes: out var removes));
        Assert.True(condition: removes);
        Assert.StartsWith(expectedStartString: "[gpu.faults: armed=pipeline:2,remove:1 ", actualString: registry.Submit(line: "gpu.faults list").Output);
        Assert.EndsWith(expectedEndString: " frames=0]", actualString: registry.Submit(line: "gpu.faults disarm").Output);
        Assert.False(condition: faults.TryGetArmedLoss(remaining: out _, removes: out _));
    }
    [InlineData("gpu.faults remove 0")]
    [InlineData("gpu.faults remove 1 2")]
    [InlineData("gpu.faults lose x")]
    [InlineData("gpu.faults arm nothing")]
    [InlineData("gpu.faults arm pipeline 0")]
    [InlineData("gpu.faults list extra")]
    [InlineData("gpu.faults wobble")]
    [Theory]
    public void AMalformedFormAnswersItsUsage(string line) {
        var (registry, _) = Console();
        var result = registry.Submit(line: line);

        Assert.True(condition: result.IsError);
        Assert.Contains(expectedSubstring: "remove [<n>]", actualString: result.Output);
    }
}
