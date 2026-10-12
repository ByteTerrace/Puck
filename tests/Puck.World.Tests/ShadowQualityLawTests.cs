using System.Text.Json.Serialization.Metadata;

using Puck.Abstractions.Presentation;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Transpiler;

using Xunit;

namespace Puck.World.Tests;

/// <summary>CPU laws for authored shadow quality rows and their live lever, save and boot path.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class ShadowQualityLawTests {
    private sealed class AudioLever : IWorldAudioLever {
        public float? SessionMasterVolume { get; private set; }

        public void SetMasterVolume(float value) => SessionMasterVolume = value;
    }

    private static WorldSessionLever RoundTrip(WorldSessionLever lever) {
        Assert.True(condition: WorldSubmissionCodec.TryEncodeLever(bytes: out var bytes, failure: out var encoded, lever: lever),
            userMessage: encoded.Detail);
        Assert.True(condition: WorldSubmissionCodec.TryDecodeLever(bytes, lever: out var decoded, failure: out var failure),
            userMessage: failure.Detail);
        Assert.Equal(actual: decoded, expected: lever);

        return decoded;
    }

    [InlineData(0, 0, 0u, WorldShadowOverflow.Instant)]
    [InlineData(4, 2, uint.MaxValue, WorldShadowOverflow.Queue)]
    [Theory]
    public void ShadowPolicyRoundTripsThroughTheNamedLeverSinkAndSavedBoot(int slots, int fades, uint ticks, WorldShadowOverflow overflow) {
        var authored = new WorldRenderDefaults(ShadowLights: 1, ShadowFadeSlots: 1, ShadowFadeTicks: 1, ShadowOverflow: WorldShadowOverflow.Instant);
        var definition = Fixtures.BuildDocument() with { RenderRaw = authored };
        var settings = new WorldRenderSettings(defaults: definition.Render);
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new AudioLever();
        var bindingBar = new WorldBindingBarVisibility();
        var sink = WorldSessionLevers.Compose(audio: audio, bindingBar: bindingBar, pacing: pacing, settings: settings);
        var expected = new WorldShadowSettings(FadeSlots: fades, FadeTicks: ticks, Overflow: overflow, Slots: slots);

        Assert.Equal(expected: new WorldShadowSettings(FadeSlots: 1, FadeTicks: 1, Overflow: WorldShadowOverflow.Instant, Slots: 1), actual: settings.ShadowSlots);
        Assert.True(condition: sink.TryApply(lever: RoundTrip(lever: new WorldSessionLever(
            Section: WorldSection.Render, Name: WorldSessionLevers.ShadowSlots, A: slots, B: fades, C: ticks, D: ((int)overflow)))));
        Assert.Equal(expected: expected, actual: settings.ShadowSlots);
        Assert.Same(expected: authored, actual: definition.Render);

        var saved = WorldSessionLevers.Fold(definition: definition, settings: settings, pacing: pacing, audio: audio,
            bindingBar: bindingBar, editor: new WorldEditorSeats());

        Assert.Equal(expected: expected, actual: WorldShadowSettings.From(render: saved.Render));
        Assert.Equal(expected: expected, actual: new WorldRenderSettings(defaults: saved.Render).ShadowSlots);
        Assert.Same(expected: saved.Render, actual: WorldSessionLevers.Fold(definition: saved, settings: settings,
            pacing: pacing, audio: audio, bindingBar: bindingBar, editor: new WorldEditorSeats()).Render);
    }
    [Fact]
    public void SwitchingPresetsInstallsAllFourShadowFieldsInExactlyOneSettingsChange() {
        var fading = new WorldQualityPreset(Shadows: ShadowTier.High, AmbientOcclusion: false,
            RenderScale: 1f, ShadowLights: 4, ShadowFadeSlots: 2,
            ShadowFadeTicks: 30, ShadowOverflow: WorldShadowOverflow.Queue);
        var instant = fading with { ShadowLights = 2, ShadowFadeSlots = 0, ShadowFadeTicks = 0, ShadowOverflow = WorldShadowOverflow.Instant };
        var definition = Fixtures.BuildDocument() with { RenderRaw = new WorldRenderDefaults() };
        var settings = new WorldRenderSettings(defaults: definition.Render);
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new AudioLever();
        var bindingBar = new WorldBindingBarVisibility();
        var sink = WorldSessionLevers.Compose(audio: audio, bindingBar: bindingBar, pacing: pacing, settings: settings);

        foreach (var preset in new[] { fading, instant, fading }) {
            var revision = settings.Revision;

            Assert.True(condition: sink.TryApply(lever: RoundTrip(lever: WorldSessionLevers.ShadowPolicy(preset: preset))));
            Assert.Equal(expected: (revision + 1), actual: settings.Revision);
            Assert.Equal(expected: new WorldShadowSettings(preset.ShadowLights, preset.ShadowFadeSlots, preset.ShadowFadeTicks, preset.ShadowOverflow),
                actual: settings.ShadowSlots);
            var saved = WorldSessionLevers.Fold(definition: definition, settings: settings, pacing: pacing,
                audio: audio, bindingBar: bindingBar, editor: new WorldEditorSeats());

            Assert.True(condition: WorldDefinitionLoader.TryLoad(utf8: WorldDefinitionSerialization.Serialize(definition: saved),
                sourceName: "s60-fix-presets.world.json", definition: out _, reason: out var reason), userMessage: reason);
        }
    }
    [InlineData(QualityTier.Low, 0)]
    [InlineData(QualityTier.Medium, 1)]
    [InlineData(QualityTier.High, 2)]
    [Theory]
    public void SharedQualitySourceAuthorsTheCurrentInstantShadowPolicy(QualityTier tier, int slots) {
        var compilation = WorldCompiler.CompileFile(allowMultiple: true, cancellationToken: TestContext.Current.CancellationToken,
            path: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/quality.puck"));

        Assert.True(condition: compilation.Success, userMessage: "quality.puck must compile before its policy is inspected.");
        Assert.True(condition: WorldJsonPayload.TryParse(
            info: ((JsonTypeInfo<WorldRenderDefaults>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldRenderDefaults))),
            json: compilation.RequireJson()["render"]!.ToJsonString(),
            value: out var table,
            error: out var error), userMessage: error);
        var preset = table.Preset(tier: tier);

        Assert.True(condition: preset.HasValue);
        Assert.Equal(expected: slots, actual: preset.Value.ShadowLights);
        Assert.Equal(expected: 0, actual: preset.Value.ShadowFadeSlots);
        Assert.Equal(expected: 0u, actual: preset.Value.ShadowFadeTicks);
        Assert.Equal(expected: WorldShadowOverflow.Instant, actual: preset.Value.ShadowOverflow);
        Assert.Equal(expected: (tier != QualityTier.Low), actual: preset.Value.ShadowAmortize);
    }
}
