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

[Collection(SceneProbeCollection.Name)]
public sealed class WorldSkyLayerLawTests {
    [Fact]
    public void RunningWorldCommandAuditionsExistingRowsAndRefusesUnknownOnes() {
        using var files = new TemporaryDirectory();
        var host = files.Own(owner: WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json", definition => definition with {
                RenderRaw = definition.Render with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Noise(Coverage: 0.125f)]) },
            }).Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var before = WorldDefinitionSerialization.Serialize(definition: server.Definition);

        Assert.False(condition: registry.Submit(line: "world.sky-layer solo 0").IsError);
        Assert.Equal(0, settings.SkyLayers.Solo);
        Assert.False(condition: registry.Submit(line: "world.sky-layer mute 0 on").IsError);
        Assert.True(condition: settings.SkyLayers.Muted(index: 0));
        Assert.Contains("included=False", registry.Submit(line: "world.sky-layer").Output);
        Assert.False(condition: registry.Submit(line: "world.sky-layer solo off").IsError);
        Assert.Equal(-1, settings.SkyLayers.Solo);
        Assert.True(condition: registry.Submit(line: "world.sky-layer solo 1").IsError);
        Assert.True(condition: registry.Submit(line: "world.sky-layer mute -1 on").IsError);
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition: server.Definition));
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
        var settings = new WorldRenderSettings(defaults: definition.Render);
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new Audio();
        var bar = new WorldBindingBarVisibility();
        var sink = WorldSessionLevers.Compose(audio: audio, bindingBar: bar, pacing: pacing, settings: settings);
        var mirror = ClientFixtures.StateMirror(definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        WorldResolvedEnvironment Resolve() => resolver.Resolve(definition, 0, mirror, layers: settings.SkyLayers);
        void Apply(string name, double a, double b = 0d) {
            var lever = new WorldSessionLever(Section: WorldSection.Render, Name: name, A: a, B: b);

            Assert.True(condition: WorldSubmissionCodec.TryEncodeLever(bytes: out var bytes, failure: out _, lever: lever));
            Assert.True(condition: WorldSubmissionCodec.TryDecodeLever(bytes: bytes, failure: out _, lever: out var decoded));
            Assert.True(condition: sink.TryApply(lever: decoded));
        }
        var full = Resolve().Sky;

        Assert.Equal(0.125f, full.Atmosphere.FogDensity);
        Assert.Equal(4, full.LayerCount);
        Assert.Equal(0.25f, full.Parameters<SdfSkyStars>(index: 1).Brightness);
        Assert.Equal(2f, full.Parameters<SdfSkyStars>(index: 2).Brightness);
        Apply(WorldSessionLevers.SkySolo, 1);
        var solo = Resolve().Sky;

        Assert.Equal(1, solo.LayerCount);
        Assert.Equal("bright", solo.LabelAt(index: 0));
        Assert.Equal(2f, solo.First<SdfSkyStars>().Brightness);
        Assert.Equal(0.125f, solo.Atmosphere.FogDensity);
        Apply(a: 1, b: 1d, name: WorldSessionLevers.SkyMute);
        Assert.Equal(0, Resolve().Sky.LayerCount);
        Apply(WorldSessionLevers.SkySolo, -1);
        var muted = Resolve().Sky;

        Assert.Equal(3, muted.LayerCount);
        Assert.Equal("faint", muted.LabelAt(index: 1));
        Assert.Equal(0.25f, muted.First<SdfSkyStars>().Brightness);
        Assert.Equal(0.125f, muted.Atmosphere.FogDensity);
        Assert.Equal(0.75f, muted.First<SdfSkyClouds>().Coverage);
        Apply(WorldSessionLevers.SkyMute, 1);
        Assert.Equal(2f, Resolve().Sky.Parameters<SdfSkyStars>(index: 2).Brightness);
        var resolutions = resolver.Resolutions;

        _ = Resolve();
        Assert.Equal(resolutions, resolver.Resolutions);
        var saved = WorldSessionLevers.Fold(definition, settings, pacing, audio, bar, new WorldEditorSeats());

        Assert.Same(definition.Render.Sky, saved.Render.Sky);
        Assert.Equal(-1, new WorldRenderSettings(defaults: saved.Render).SkyLayers.Solo);
    }
    [Fact]
    public void MutedAutomaticDiscStaysMutedAfterShadowSelection() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Intensity: 1f)])),
        };
        var mirror = ClientFixtures.StateMirror(definition);
        var layers = new WorldSkyAudition();
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.True(condition: (resolver.Resolve(definition, 0, mirror, layers: layers).Sky.First<SdfSkyDisc>().Light >= 0));
        layers.SetMuted(index: 0, muted: true);
        Assert.True(condition: (resolver.Resolve(definition, 0, mirror, layers: layers).Sky.IndexOf(kind: SdfSkyLayerKind.Disc) == -1),
            userMessage: "Automatic light selection must not re-enable a muted sun disc.");
        layers.SetMuted(index: 0, muted: false);
        Assert.True(condition: (resolver.Resolve(definition, 0, mirror, layers: layers).Sky.First<SdfSkyDisc>().Light >= 0));
    }
    [Fact]
    public void SkyCostIsAnAddressableDebugView() {
        Assert.True(condition: DebugViewModes.TryParse(mode: out var mode, name: "sky-cost"));
        Assert.Equal(actual: mode, expected: DebugViewModes.SkyCost);
        Assert.Equal("sky-cost", DebugViewModes.Name(mode: mode));
        Assert.Equal(actual: mode, expected: 13);
    }
}
