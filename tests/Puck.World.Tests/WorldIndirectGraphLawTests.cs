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
    public void SharedViewsReadOneBufferProducerAndOffAddsNothing() {
        var views = new[] {
            new RenderGraphInstance("world", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new RenderGraphInstance("world$1", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new RenderGraphInstance("remote", RenderGraphRefresh.EveryFrame, 1, [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
        };

        Assert.True(RenderGraphInstanceSet.TryCreate(views, out var set, out _));
        Assert.Same(set, WorldIndirectGraph.Append(set, new Dictionary<string, string>()));
        var enabled = WorldIndirectGraph.Append(set, new Dictionary<string, string> {
            ["world"] = "indirect:world",
            ["world$1"] = "indirect:world",
            ["remote"] = "indirect:remote",
        });

        Assert.Equal(5, enabled.Instances.Count);
        Assert.All(enabled.Instances.Take(3), instance => {
            var edge = Assert.Single(instance.Reads);

            Assert.Equal(ShaderPipelineResourceKind.Buffer, edge.Kind);
            Assert.False(edge.PreviousFrame);
            var producer = enabled.Instances.Single(item => (item.Name == edge.Producer));

            Assert.Equal(ShaderPipelineResourceKind.Buffer, producer.Output);
            Assert.Equal("indirect", producer.ExternalPackage);
            Assert.True((enabled.Order.ToList().IndexOf(enabled.Instances.ToList().IndexOf(producer)) <
                enabled.Order.ToList().IndexOf(enabled.Instances.ToList().IndexOf(instance))));
        });
    }
    [Fact]
    public void SessionTierAppliesAllThreeValuesAndRefusesUnknownOrdinals() {
        var settings = new WorldRenderSettings(new WorldRenderDefaults());
        var sink = WorldSessionLevers.Compose(settings, new PresentPacingControl(null), new Audio(), new WorldBindingBarVisibility());

        Assert.Equal(SdfIndirectTier.Off, settings.IndirectTier);
        foreach (var tier in Enum.GetValues<SdfIndirectTier>()) {
            var before = settings.Revision;

            Assert.True(sink.TryApply(new(WorldSection.Render, WorldSessionLevers.Indirect, ((int)tier))));
            Assert.Equal(tier, settings.IndirectTier);
            Assert.True((settings.Revision > before));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.IndirectTier = ((SdfIndirectTier)3));
    }
    [Fact]
    public void IndirectDebugNamesReachTheirShaderModeIndices() {
        Assert.True(DebugViewModes.TryParse("indirect-probes", out var probes));
        Assert.True(DebugViewModes.TryParse("indirect-cells", out var cells));
        Assert.Equal(13, probes);
        Assert.Equal(14, cells);
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
}
