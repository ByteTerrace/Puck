using Puck.Abstractions.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class WorldSkyCostLawTests {
    private sealed class Registry(IGpuWorkSource source) : IGpuWorkRegistry {
        public GpuDeviceCapabilities? DeviceCapabilities => null;
        public GpuDeviceIdentity? DeviceIdentity => null;

        public void CopyNodes(List<GpuWorkNode> nodes) => nodes.Add(item: new GpuWorkNode(Name: "world", Work: source, Lifetime: null));
    }
    private sealed class Readback : IGpuWorkReadback {
        public void AddTo(int slot, Span<long> counts, int rowCount) {
            var evaluations = GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyEvaluations);
            var hashes = GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyHashes);

            counts[((5 * GpuWork.SubmissionKinds.Length) + evaluations)] += 1;
            counts[((6 * GpuWork.SubmissionKinds.Length) + evaluations)] += 2;
            counts[((6 * GpuWork.SubmissionKinds.Length) + hashes)] += 64;
        }
    }

    [Fact]
    public void CostSkyPrintsCompletedLayerCountsAndPreservesSkippedAndStandingReadings() {
        var ledger = new GpuWorkLedger(framesInFlight: 1, name: "gpu.p18-12-cost");
        var services = GpuWorkCounting.Wrap(new FakeGpuDevice().Services, ledger);

        ledger.Configure(1, ["sdf.world$sky", "sdf.world$composite", "sdf.world$views"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "gradient", Pass: 0), new(Detail: "clouds", Pass: 0), new(Detail: "plain", Pass: 1), new(Detail: "stars", Pass: 1)]);
        using var files = new TemporaryDirectory();
        var builder = WorldBootHarness.Compose(files, WorldHostPresentation.None, "tests/Puck.World.Canaries/editor-grid/fixture.world.json");

        builder.Services.AddSingleton<IGpuWorkRegistry>(implementationInstance: new Registry(source: ledger));
        var host = files.Own(owner: builder.Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();

        Assert.Contains("work unavailable", registry.Submit(line: "world.cost sky").Output);
        ledger.EnterPass(pass: 0);
        services.Recorder.Dispatch(commandBufferHandle: 1, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
        ledger.LeavePass();
        ledger.SkipPass(pass: 1);
        ledger.EnterPass(pass: 2);
        services.Recorder.Dispatch(commandBufferHandle: 1, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
        ledger.LeavePass();
        ledger.ReadOnCompletion(readback: new Readback(), slot: 0);
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);
        var result = registry.Submit(line: "world.cost sky");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Contains("work submission=1 revision=1", result.Output);
        Assert.Contains("sky.evaluations=3", result.Output);
        var clouds = Assert.Single(collection: result.Output!.Split('\n'), predicate: line => line.Contains(comparisonType: StringComparison.Ordinal, value: "detail=clouds"));

        Assert.Contains(actualString: clouds, expectedSubstring: "sky.evaluations=2");
        Assert.Contains(actualString: clouds, expectedSubstring: "sky.hashes=64");
        Assert.Contains("work sdf.world$composite detail=stars skipped\n", result.Output);
        Assert.DoesNotContain("sdf.world$views", result.Output);
        Assert.DoesNotContain("work outside", result.Output);
        Assert.Equal(result.Output, registry.Submit(line: "world.cost sky").Output);
        Assert.True(condition: registry.Submit(line: "world.cost sky extra").IsError);
    }
}
