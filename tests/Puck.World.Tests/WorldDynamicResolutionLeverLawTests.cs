using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldDynamicResolutionLeverLawTests {
    [Fact]
    public void BootConsoleAndQualityUseTheSameDynamicResolutionSettingWithoutADevice() {
        using var directory = new TemporaryDirectory(prefix: "puck-dynamic-lever-");
        using var host = WorldBootHarness.Compose(stateDirectory: directory, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.Counters/counters.world.json", edit: static definition => definition with {
                RenderRaw = definition.Render with { DynamicResolution = true },
            }).Build();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        // The executable's post-build wiring attaches the accepted-lever sink before it starts the host.
        host.Services.GetRequiredService<WorldClient>().AttachSessionLevers(levers: host.Services.GetRequiredService<WorldSessionLeverSink>());
        void Submit(string line) {
            var result = registry.Submit(line: line);
            Assert.False(condition: result.IsError, userMessage: result.Output);
        }
        Assert.True(condition: settings.DynamicResolution);
        Submit(line: "world.dynamic-resolution off");
        Assert.False(condition: settings.DynamicResolution);
        Submit(line: "world.dynamic-resolution on");
        Assert.True(condition: settings.DynamicResolution);
        Submit(line: "world.quality low");
        Assert.False(condition: settings.DynamicResolution);
        Assert.True(condition: registry.Submit(line: "world.dynamic-resolution maybe").IsError);
        Assert.False(condition: settings.DynamicResolution);
    }
    [Fact]
    public void ASessionSaveFoldsTheToggleIntoAnAuthoredRenderSection() {
        var definition = Fixtures.BuildDocument() with { RenderRaw = new WorldRenderDefaults(), };
        var settings = new WorldRenderSettings(defaults: definition.Render) { DynamicResolution = true, };
        var folded = WorldSessionLevers.Fold(definition: definition, settings: settings,
            pacing: new PresentPacingControl(initialTargetHertz: null), audio: new Audio(),
            bindingBar: new WorldBindingBarVisibility(), editor: new WorldEditorSeats());
        Assert.True(condition: folded.Render.DynamicResolution);
        Assert.False(condition: definition.Render.DynamicResolution);
        settings.DynamicResolution = false;
        folded = WorldSessionLevers.Fold(definition: folded, settings: settings,
            pacing: new PresentPacingControl(initialTargetHertz: null), audio: new Audio(),
            bindingBar: new WorldBindingBarVisibility(), editor: new WorldEditorSeats());
        Assert.False(condition: folded.Render.DynamicResolution);
    }
    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;
        public void SetMasterVolume(float value) { }
    }
}
