using Puck.Launcher;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldSkyLayerLawTests {
    [Fact]
    public void RunningWorldCommandAuditionsExistingRowsAndRefusesUnknownOnes() {
        using var files = new TemporaryDirectory();
        var host = files.Own(WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json", definition => definition with {
                RenderRaw = definition.Render with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Noise(Coverage: 0.125f)]) },
            }).Build());

        Assert.True(WorldPostBuildWiring.Install(host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var before = WorldDefinitionSerialization.Serialize(server.Definition);

        Assert.False(registry.Submit("world.sky-layer solo 0").IsError);
        Assert.Equal(0, settings.SkyLayers.Solo);
        Assert.False(registry.Submit("world.sky-layer mute 0 on").IsError);
        Assert.True(settings.SkyLayers.Muted(0));
        Assert.Contains("included=False", registry.Submit("world.sky-layer").Output);
        Assert.False(registry.Submit("world.sky-layer solo off").IsError);
        Assert.Equal(-1, settings.SkyLayers.Solo);
        Assert.True(registry.Submit("world.sky-layer solo 1").IsError);
        Assert.True(registry.Submit("world.sky-layer mute -1 on").IsError);
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(server.Definition));
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }

    [Fact]
    public void SoloAndMuteResolveThroughTheLeverSinkAndNeverSave() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.125f)), Sky: new WorldRenderSky(Layers: [
                new WorldRenderSkyLayer.Stars(Name: "faint", Brightness: 0.25f),
                new WorldRenderSkyLayer.Stars(Name: "bright", Brightness: 2f),
                new WorldRenderSkyLayer.Clouds(Coverage: 0.75f),
            ])),
        };
        var settings = new WorldRenderSettings(definition.Render);
        var pacing = new PresentPacingControl(null);
        var audio = new Audio();
        var bar = new WorldBindingBarVisibility();
        var sink = WorldSessionLevers.Compose(settings, pacing, audio, bar);
        var mirror = ClientFixtures.StateMirror(definition);
        using var resolver = new WorldEnvironmentResolve(new WorldValueDomainGuard());

        WorldResolvedEnvironment Resolve() => resolver.Resolve(definition, 0, mirror, layers: settings.SkyLayers);
        void Apply(string name, double a, double b = 0d) {
            var lever = new WorldSessionLever(Section: WorldSection.Render, Name: name, A: a, B: b);

            Assert.True(WorldSubmissionCodec.TryEncodeLever(lever, out var bytes, out _));
            Assert.True(WorldSubmissionCodec.TryDecodeLever(bytes, out var decoded, out _));
            Assert.True(sink.TryApply(decoded));
        }
        var full = Resolve().Sky;

        Assert.Equal(0.125f, full.Atmosphere.FogDensity);
        Assert.Equal(4, full.LayerCount);
        Assert.Equal(0.25f, full.Parameters<SdfSkyStars>(1).Brightness);
        Assert.Equal(2f, full.Parameters<SdfSkyStars>(2).Brightness);
        Apply(WorldSessionLevers.SkySolo, 1);
        var solo = Resolve().Sky;

        Assert.Equal(1, solo.LayerCount);
        Assert.Equal("bright", solo.LabelAt(0));
        Assert.Equal(2f, solo.First<SdfSkyStars>().Brightness);
        Assert.Equal(0.125f, solo.Atmosphere.FogDensity);
        Apply(WorldSessionLevers.SkyMute, 1, 1d);
        Assert.Equal(0, Resolve().Sky.LayerCount);
        Apply(WorldSessionLevers.SkySolo, -1);
        var muted = Resolve().Sky;

        Assert.Equal(3, muted.LayerCount);
        Assert.Equal("faint", muted.LabelAt(1));
        Assert.Equal(0.25f, muted.First<SdfSkyStars>().Brightness);
        Assert.Equal(0.125f, muted.Atmosphere.FogDensity);
        Assert.Equal(0.75f, muted.First<SdfSkyClouds>().Coverage);
        Apply(WorldSessionLevers.SkyMute, 1);
        Assert.Equal(2f, Resolve().Sky.Parameters<SdfSkyStars>(2).Brightness);
        var resolutions = resolver.Resolutions;

        _ = Resolve();
        Assert.Equal(resolutions, resolver.Resolutions);
        var saved = WorldSessionLevers.Fold(definition, settings, pacing, audio, bar, new WorldEditorSeats());

        Assert.Same(definition.Render.Sky, saved.Render.Sky);
        Assert.Equal(-1, new WorldRenderSettings(saved.Render).SkyLayers.Solo);
    }
    [Fact]
    public void MutedAutomaticDiscStaysMutedAfterShadowSelection() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Intensity: 1f)])),
        };
        var mirror = ClientFixtures.StateMirror(definition);
        var layers = new WorldSkyAudition();
        using var resolver = new WorldEnvironmentResolve(new WorldValueDomainGuard());

        Assert.True((resolver.Resolve(definition, 0, mirror, layers: layers).Sky.First<SdfSkyDisc>().Light >= 0));
        layers.SetMuted(0, true);
        Assert.True((resolver.Resolve(definition, 0, mirror, layers: layers).Sky.IndexOf(SdfSkyLayerKind.Disc) == -1),
            "Automatic light selection must not re-enable a muted sun disc.");
        layers.SetMuted(0, false);
        Assert.True((resolver.Resolve(definition, 0, mirror, layers: layers).Sky.First<SdfSkyDisc>().Light >= 0));
    }
    [Fact]
    public void SkyCostIsAnAddressableDebugView() {
        Assert.True(DebugViewModes.TryParse("sky-cost", out var mode));
        Assert.Equal(DebugViewModes.SkyCost, mode);
        Assert.Equal("sky-cost", DebugViewModes.Name(mode));
        Assert.Equal(13, mode);
    }
}
