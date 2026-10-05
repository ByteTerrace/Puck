using Puck.Hosting;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The host composes its environment before the world view; the session tier and graph edges own one cache
/// per residency and none while off. Buffer producers use their package's sizing contract.</summary>
public sealed class WorldIndirectGraphLawTests {
    [Fact]
    public void TheHostComposesItsEnvironmentBufferBeforeTheWorldView() {
        using var directory = new TemporaryDirectory(prefix: "world-environment-graph-");
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
            kernels: SdfTestPipelines.Kernels(), name: "world", width: 1, height: 1, brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        using var environment = new SdfSkyEnvironmentPasses(views);
        using var host = new WorldViewGraphHost(documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: directory.PathOf("pipelines")))) {
            Pickers = views, Environment = environment,
        };
        using var instances = FakeGraphInstances.Attach(host: host, create: static name => new ShaderPipelineRenderNode(
            deviceContext: new RefusingGpuDevice(), height: 4, hostsOnDirectX: false, name: name,
            pipelines: new GpuPassPipelineCache(), width: 4));

        host.Reconcile(views: new WorldViewDefaults());

        var graph = instances.Instances;
        var producer = Assert.Single(graph.Instances, instance => instance.ExternalPackage == RenderGraphPackageCatalog.SkyEnvironment);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, producer.Output);
        var world = Assert.Single(graph.Instances, instance => instance.ExternalPackage == RenderGraphPackageCatalog.SdfWorld);
        var read = Assert.Single(world.Reads, edge => edge.Producer == producer.Name);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, read.Kind);
        Assert.False(read.PreviousFrame);
        Assert.True(graph.Order.ToList().IndexOf(graph.IndexOf(producer.Name)) < graph.Order.ToList().IndexOf(graph.IndexOf(world.Name)));
    }

    [Fact]
    public void TheSharedEnvironmentRunsBeforeTheFiniteSolvesPinPass() {
        Assert.True(RenderGraphInstanceSet.TryCreate([
            new(Name: "environment", Refresh: RenderGraphRefresh.EveryFrame, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count, Reads: [],
                Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment),
            new(Name: "world", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1,
                Reads: [new("environment", Kind: ShaderPipelineResourceKind.Buffer)], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new(Name: "world$1", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1,
                Reads: [new("environment", Kind: ShaderPipelineResourceKind.Buffer)], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
        ], out var set, out var refusal), refusal?.Message);
        var graph = WorldIndirectGraph.Append(set, new Dictionary<string, string> { ["world"] = "cache", ["world$1"] = "cache" });
        var cache = Assert.Single(graph.Instances, instance => instance.ExternalPackage == RenderGraphPackageCatalog.Indirect);
        Assert.Equal(5, cache.Passes);
        var environment = Assert.Single(cache.Reads, read => read.Producer == "environment");
        Assert.False(environment.PreviousFrame);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, environment.Kind);
        Assert.True(graph.Order.ToList().IndexOf(graph.IndexOf("environment")) < graph.Order.ToList().IndexOf(graph.IndexOf("cache")));
    }

    [Fact]
    public void SharedViewsReadOneBufferProducerAndOffAddsNothing() {
        var views = new[] {
            new RenderGraphInstance("world", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new RenderGraphInstance("world$1", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new RenderGraphInstance("remote", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
        };

        Assert.True(condition: RenderGraphInstanceSet.TryCreate(views, out var set, out _));
        Assert.Same(set, WorldIndirectGraph.Append(set, new Dictionary<string, string>()));
        var enabled = WorldIndirectGraph.Append(set, new Dictionary<string, string> {
            ["world"] = "indirect:world",
            ["world$1"] = "indirect:world",
            ["remote"] = "indirect:remote",
        });

        Assert.Equal(7, enabled.Instances.Count);
        Assert.All(enabled.Instances.Take(count: 3), instance => {
            Assert.Equal(expected: 2, actual: instance.Reads.Count);
            foreach (var edge in instance.Reads) {
            Assert.Equal(ShaderPipelineResourceKind.Buffer, edge.Kind);
            Assert.False(condition: edge.PreviousFrame);
            var producer = enabled.Instances.Single(predicate: item => (item.Name == edge.Producer));

            Assert.Equal(ShaderPipelineResourceKind.Buffer, producer.Output);
            Assert.Contains(expected: producer.ExternalPackage, collection: new[] { RenderGraphPackageCatalog.Indirect, RenderGraphPackageCatalog.SdfWorld });
            Assert.True(condition: (enabled.Order.ToList().IndexOf(item: enabled.Instances.ToList().IndexOf(item: producer)) <
                enabled.Order.ToList().IndexOf(item: enabled.Instances.ToList().IndexOf(item: instance))));
            }
        });
        Assert.Equal(expected: 2, actual: enabled.Instances.Skip(count: 3).Count(predicate: instance => (instance.ExternalPackage == RenderGraphPackageCatalog.SdfWorld)));
    }
    [Fact]
    public void SessionTierAppliesAllThreeValuesAndRefusesUnknownOrdinals() {
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults());
        var sink = WorldSessionLevers.Compose(settings, new PresentPacingControl(initialTargetHertz: null), new Audio(), new WorldBindingBarVisibility());

        Assert.Equal(SdfIndirectTier.Medium, settings.IndirectTier);
        foreach (var tier in Enum.GetValues<SdfIndirectTier>()) {
            Assert.Equal(tier, new WorldRenderSettings(new WorldRenderDefaults(Indirect: new(Tier: tier))).IndirectTier);
        }
        foreach (var tier in Enum.GetValues<SdfIndirectTier>()) {
            var before = settings.Revision;

            Assert.True(condition: sink.TryApply(lever: new(WorldSection.Render, WorldSessionLevers.Indirect, ((int)tier))));
            Assert.Equal(tier, settings.IndirectTier);
            Assert.True(condition: (settings.Revision > before));
        }
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => settings.IndirectTier = ((SdfIndirectTier)3));
    }
    [Fact]
    public void IndirectDebugNamesReachTheirShaderModeIndices() {
        Assert.True(condition: DebugViewModes.TryParse(mode: out var probes, name: "indirect-probes"));
        Assert.True(condition: DebugViewModes.TryParse(mode: out var cells, name: "indirect-cells"));
        Assert.True(condition: DebugViewModes.TryParse(mode: out var light, name: "indirect-light"));
        Assert.Equal(actual: probes, expected: 14);
        Assert.Equal(actual: cells, expected: 15);
        Assert.Equal(actual: light, expected: 16);
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
    private sealed class UncapturedSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            throw new InvalidOperationException("Graph composition needs no captured frame or GPU submission.");
    }
}
