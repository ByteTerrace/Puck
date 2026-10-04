using Puck.Hosting;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The session tier and graph edges own one cache per residency and none while off.</summary>
public sealed class WorldIndirectGraphLawTests {
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

        Assert.Equal(SdfIndirectTier.Off, settings.IndirectTier);
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
}
