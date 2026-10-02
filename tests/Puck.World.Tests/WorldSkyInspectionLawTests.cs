using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Launcher;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Sky inspection retains an immutable captured selection, while quality uses the existing session lever.</summary>
public sealed partial class WorldSkyInspectionLawTests {
    [Fact]
    public void SoloWinsAndClearingItRestoresMutesWithoutChangingCapturedViews() {
        var muted = SdfSkyInspection.None.WithMute(layer: "clouds", muted: true).WithMute(layer: "stars", muted: true);
        var solo = muted.WithSolo(layer: "stars");

        Assert.False(condition: muted.Allows(layer: "stars"));
        Assert.True(condition: solo.Allows(layer: "stars"));
        Assert.False(condition: solo.Allows(layer: "gradient"));
        Assert.Equal(new[] { "clouds", "stars" }, solo.Muted.ToArray());
        var edited = solo.WithMute(layer: "clouds", muted: false).WithMute(layer: "gradient", muted: true);
        var restored = edited.WithSolo(layer: null);

        Assert.True(condition: edited.Allows(layer: "stars"));
        Assert.True(condition: restored.Allows(layer: "clouds"));
        Assert.False(condition: restored.Allows(layer: "stars"));
        Assert.False(condition: restored.Allows(layer: "gradient"));
        Assert.False(condition: muted.Allows(layer: "clouds"));
        Assert.True(condition: muted.Allows(layer: "gradient"));
        Assert.Same(restored, restored.WithSolo(layer: null));
        Assert.Same(restored, restored.WithMute(layer: "stars", muted: true));
        Assert.Same(SdfSkyInspection.None, restored.WithMute(layer: "stars", muted: false).WithMute(layer: "gradient", muted: false));
        Assert.Equal(0L, AllocationWindow.Least(() => {
            for (var index = 0; (index < 256); index++) {
                _ = solo.Allows(layer: "stars");
                _ = solo.Allows(layer: "clouds");
                _ = restored.Allows(layer: "clouds");
                _ = restored.WithMute(layer: "stars", muted: true);
            }
        }));
    }
    [Fact]
    public void QualityLeverAcceptsOnlyDeclaredTiersAndAutoAndNeverFoldsIntoTheDocument() {
        var definition = Fixtures.BuildDocument();
        var settings = new WorldRenderSettings(defaults: definition.Render);
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new Audio();
        var visibility = new WorldBindingBarVisibility();
        var editor = new WorldEditorSeats();
        var sink = WorldSessionLevers.Compose(audio: audio, bindingBar: visibility, pacing: pacing, settings: settings);
        var original = WorldSessionLevers.Fold(audio: audio, bindingBar: visibility, definition: definition, editor: editor, pacing: pacing, settings: settings);

        Assert.Null(value: settings.SkyQuality);
        Assert.True(condition: sink.IsRegistered(name: WorldSessionLevers.SkyQuality));
        foreach (var tier in QualityTiers.All) {
            sink.Apply(lever: new WorldSessionLever(Name: WorldSessionLevers.SkyQuality, Section: WorldSection.Render, A: ((double)tier)));
            Assert.Equal(tier, settings.SkyQuality);
            var revision = settings.Revision;

            sink.Apply(lever: new WorldSessionLever(Name: WorldSessionLevers.SkyQuality, Section: WorldSection.Render, A: ((double)tier)));
            Assert.Equal(revision, settings.Revision);
            Assert.Equal(original, WorldSessionLevers.Fold(audio: audio, bindingBar: visibility, definition: definition, editor: editor, pacing: pacing, settings: settings));
        }
        var priorRevision = settings.Revision;

        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -2d, 0.5d, 3d, 256d }) {
            sink.Apply(lever: new WorldSessionLever(Name: WorldSessionLevers.SkyQuality, Section: WorldSection.Render, A: invalid));
            Assert.Equal(QualityTier.High, settings.SkyQuality);
            Assert.Equal(priorRevision, settings.Revision);
        }
        sink.Apply(lever: new WorldSessionLever(Name: WorldSessionLevers.SkyQuality, Section: WorldSection.Render, A: WorldSessionLevers.SkyQualityAuto));
        Assert.Null(value: settings.SkyQuality);
        Assert.Equal((priorRevision + 1), settings.Revision);
        Assert.Equal(original, WorldSessionLevers.Fold(audio: audio, bindingBar: visibility, definition: definition, editor: editor, pacing: pacing, settings: settings));
    }
    [Fact]
    public void RegisteredDottedQualityCommandAppliesAndEchoesTheLiveLever() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build();

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();

        Assert.Equal("[world.sky.quality: auto]", registry.Submit(line: "world.sky.quality").Output);
        foreach (var tier in QualityTiers.All) {
            var name = QualityTiers.Name(tier: tier);
            var result = registry.Submit(line: ("world.sky.quality " + name));

            Assert.False(condition: result.IsError, userMessage: result.Output);
            Assert.Equal(tier, settings.SkyQuality);
            Assert.Equal((("[world.sky.quality: " + name) + "]"), result.Output);
            Assert.Equal(result.Output, registry.Submit(line: "world.sky.quality").Output);
        }
        Assert.True(condition: registry.Submit(line: "world.sky.quality extreme").IsError);
        Assert.True(condition: registry.Submit(line: "world.sky.quality low high").IsError);
        Assert.Equal(QualityTier.High, settings.SkyQuality);
        var cleared = registry.Submit(line: "world.sky.quality auto");

        Assert.False(condition: cleared.IsError, userMessage: cleared.Output);
        Assert.Null(value: settings.SkyQuality);
        Assert.Equal("[world.sky.quality: auto]", cleared.Output);
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
}
