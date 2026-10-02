using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldDynamicResolutionBudgetLawTests {
    [Fact]
    public void TheShippedBudgetReadsTheCommittedRecordingAndWorkloadScale() {
        using var ceilingsStream = File.OpenRead(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.Counters/counters.ceilings.json"));
        using var workloadStream = File.OpenRead(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.Counters/counters.world.json"));
        var ceilings = JsonSerializer.Deserialize(utf8Json: ceilingsStream, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)!;
        var world = JsonSerializer.Deserialize(utf8Json: workloadStream, jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)!;
        var scale = WorldRenderScaleTiers.Scale(tier: world.Render!.LowRaw!.Value.RenderScale);
        var expected = ceilings.Runs.Max(selector: static run =>
            (run.Ceilings.Where(predicate: static row => (row.Kind == "gpu.march.steps")).Sum(selector: static row => row.Ceiling) /
                (((double)run.Width) * run.Height)));

        Assert.Equal(expected: ((expected * 1920) * 1080),
            actual: WorldDynamicResolutionBudget.Recorded.For(ceiling: scale, height: 1080, width: 1920), precision: 6);
    }
    [Fact]
    public void RerecordingEitherBackendUpdatesTheCommonAreaScaledBudget() {
        WorldCountersCeilings Recording(long second) => new(Workload: "world", Script: "script", Runs: [
            new(Backend: "vulkan", Device: null!, Width: 100, Height: 100, Ceilings: [Row(steps: 1000)]),
            new(Backend: "directx", Device: null!, Width: 100, Height: 100, Ceilings: [Row(steps: second)]),
        ]);
        var initial = new WorldDynamicResolutionBudget(ceilings: Recording(second: 1500), recordedScale: WorldRenderScaleTier.Native);
        var updated = new WorldDynamicResolutionBudget(ceilings: Recording(second: 3000), recordedScale: WorldRenderScaleTier.Native);

        Assert.Equal(expected: 750, actual: initial.For(ceiling: 0.5f, height: 100, width: 200), precision: 6);
        Assert.Equal(expected: 1500, actual: updated.For(ceiling: 0.5f, height: 100, width: 200), precision: 6);
        Assert.Equal(expected: 3000, actual: updated.For(ceiling: 1, height: 100, width: 100), precision: 6);
        var halfRecording = new WorldDynamicResolutionBudget(ceilings: Recording(second: 1500), recordedScale: WorldRenderScaleTier.Half);

        Assert.Equal(expected: ((1500 * 16d) / 9d), actual: halfRecording.For(ceiling: 1, height: 100, width: 100), precision: 6);
        Assert.Equal(expected: 1500, actual: halfRecording.For(width: 100, height: 100,
            ceiling: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Half)), precision: 6);
        Assert.Equal(expected: ((1500 * 9d) / 16d), actual: halfRecording.For(width: 100, height: 100,
            ceiling: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter)), precision: 6);
    }

    private static WorldCountCeiling Row(long steps) => new(Node: "world", Pass: "sdf.world$primary",
        Kind: GpuWork.MarchSteps.Name, Class: WorkClass.PerBackendDeterministic, Ceiling: steps);
}
