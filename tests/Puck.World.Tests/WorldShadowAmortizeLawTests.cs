using Microsoft.Extensions.DependencyInjection;
using Puck.Launcher;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldShadowAmortizeLawTests {
    private sealed class AudioLever : IWorldAudioLever {
        public float? SessionMasterVolume { get; private set; }

        public void SetMasterVolume(float value) => SessionMasterVolume = value;
    }
    private sealed class LeverSink(WorldSessionLeverSink sink) : IClientSink {
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) { }
        public void DeliverSessionLever(WorldSessionLever lever) => Assert.True(condition: sink.TryApply(lever: lever));
        public void DeliverSnapshot(in WorldSnapshot snapshot) { }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) { }
    }

    [Fact]
    public void FrameShadowOwnersAreTheExactAuthoredLightNames() {
        using var files = new TemporaryDirectory(prefix: "p18-13-shadow-names-");
        var host = files.Own(owner: WorldBootHarness.Compose(stateDirectory: files, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json").Build());
        var frame = host.Services.GetRequiredService<WorldFramePresenter>().CaptureFrame(
            deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64);

        Assert.Equal("east-sun", frame.Lights.ShadowSlots.Owner(slot: 0));
        Assert.Equal("west-sun", frame.Lights.ShadowSlots.Owner(slot: 1));
    }
    [Fact]
    public void TheSessionCommandReachesFrameQualityEchoAndSavedBootWithoutATemporalToggle() {
        using var files = new TemporaryDirectory(prefix: "p18-13-shadow-lever-");
        var host = files.Own(owner: WorldBootHarness.Compose(stateDirectory: files, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json",
            edit: definition => definition with { RenderRaw = definition.Render with { Temporal = true } }).Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new AudioLever();
        var bindingBar = new WorldBindingBarVisibility();
        using var lease = server.AttachSink(sink: new LeverSink(sink: WorldSessionLevers.Compose(audio: audio, bindingBar: bindingBar,
            pacing: pacing, settings: settings)));

        foreach (var enabled in new[] { true, false, true }) {
            var result = registry.Submit(line: $"world.shadow-amortize {(enabled ? "on" : "off")}");

            Assert.False(condition: result.IsError, userMessage: result.Output);
            Assert.Equal(enabled, settings.ShadowAmortize);
            Assert.True(condition: settings.Temporal);
            Assert.Equal(enabled, presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64).Views[0].Quality.ShadowAmortize);
            Assert.Contains((enabled ? "on" : "off"), registry.Submit(line: "world.shadow-amortize").Output);
        }
        Assert.True(condition: registry.Submit(line: "world.shadow-amortize invalid").IsError);
        var saved = WorldSessionLevers.Fold(definition: server.Definition, settings: settings, pacing: pacing,
            audio: audio, bindingBar: bindingBar, editor: new WorldEditorSeats());

        Assert.True(condition: saved.Render.ShadowAmortize);
    }
    [Fact]
    public void PresetsDeliverTheAmortizationRowAndSerializationKeepsIt() {
        using var files = new TemporaryDirectory(prefix: "p18-13-shadow-preset-");
        var high = new WorldQualityPreset(ShadowTier.High, false, 1f, Temporal: true, ShadowAmortize: true, ShadowLights: 2);
        var host = files.Own(owner: WorldBootHarness.Compose(stateDirectory: files, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json",
            edit: definition => definition with { RenderRaw = definition.Render with { HighRaw = high, LowRaw = high with { ShadowAmortize = false } } }).Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        using var lease = host.Services.GetRequiredService<WorldServer>().AttachSink(sink: new LeverSink(sink: WorldSessionLevers.Compose(
            audio: new AudioLever(), bindingBar: new WorldBindingBarVisibility(),
            pacing: new PresentPacingControl(initialTargetHertz: null), settings: settings)));

        foreach (var tier in new[] { "high", "low", "high" }) {
            var result = registry.Submit(line: $"world.quality {tier}");

            Assert.False(condition: result.IsError, userMessage: result.Output);
            Assert.Equal((tier == "high"), settings.ShadowAmortize);
        }
        var definition = host.Services.GetRequiredService<WorldClient>().Definition;

        Assert.True(condition: WorldDefinitionLoader.TryLoad(WorldDefinitionSerialization.Serialize(definition: definition with {
            RenderRaw = definition.Render with { ShadowAmortize = settings.ShadowAmortize },
        }), "p18-13-saved.world.json", out var saved, out var reason), userMessage: reason);
        Assert.True(condition: saved!.Render.ShadowAmortize);
        Assert.True(condition: saved.Render.HighRaw!.Value.ShadowAmortize);
    }
}
