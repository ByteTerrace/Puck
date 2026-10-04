using Puck.Abstractions.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldSkyCostLawTests {
    private sealed class Registry(IGpuWorkSource source) : IGpuWorkRegistry {
        public GpuDeviceCapabilities? DeviceCapabilities => null;
        public GpuDeviceIdentity? DeviceIdentity => null;

        public void CopyNodes(List<GpuWorkNode> nodes) => nodes.Add(new GpuWorkNode(Name: "world", Work: source, Lifetime: null));
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
        ledger.ConfigureDetails([new(0, "plain"), new(0, "gradient"), new(0, "clouds"), new(1, "plain"), new(1, "stars")]);
        using var files = new TemporaryDirectory();
        var builder = WorldBootHarness.Compose(files, WorldHostPresentation.None, "tests/Puck.World.Canaries/editor-grid/fixture.world.json");

        builder.Services.AddSingleton<IGpuWorkRegistry>(new Registry(ledger));
        var host = files.Own(builder.Build());

        Assert.True(WorldPostBuildWiring.Install(host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();

        Assert.Contains("work unavailable", registry.Submit("world.cost sky").Output);
        ledger.EnterPass(0);
        services.Recorder.Dispatch(1, 1, 1, 1);
        ledger.LeavePass();
        ledger.SkipPass(1);
        ledger.EnterPass(2);
        services.Recorder.Dispatch(1, 1, 1, 1);
        ledger.LeavePass();
        ledger.ReadOnCompletion(new Readback(), 0);
        services.QueueSubmitter.SubmitAndWait([]);
        var result = registry.Submit("world.cost sky");

        Assert.False(result.IsError, result.Output);
        Assert.Contains("work submission=1 revision=1", result.Output);
        Assert.Contains("sky.evaluations=3", result.Output);
        var clouds = Assert.Single(result.Output!.Split('\n'), line => line.Contains("detail=clouds", StringComparison.Ordinal));

        Assert.Contains("sky.evaluations=2", clouds);
        Assert.Contains("sky.hashes=64", clouds);
        Assert.Contains("work sdf.world$composite detail=stars skipped\n", result.Output);
        Assert.DoesNotContain("sdf.world$views", result.Output);
        Assert.DoesNotContain("work outside", result.Output);
        Assert.Equal(result.Output, registry.Submit("world.cost sky").Output);
        Assert.True(registry.Submit("world.cost sky extra").IsError);
    }
}
