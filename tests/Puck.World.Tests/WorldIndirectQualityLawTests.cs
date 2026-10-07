using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Launcher;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Boot, quality selection and save retain one authored indirect tier and its independent source controls.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldIndirectQualityLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void QualityPresetAndSavedBootUseTheSameIndirectTier(bool explicitOverrides) {
        var preset = new WorldQualityPreset(ShadowTier.Off, false, 1f);
        var authored = new WorldRenderIndirect(Tier: SdfIndirectTier.Off,
            Sources: new(Screens: new BindableScalar(literal: 0f)), Bounces: 1,
            Apply: new(Intensity: new BindableScalar(literal: .5f)));
        using var files = new TemporaryDirectory(prefix: "indirect-quality-");
        var builder = WorldBootHarness.Compose(stateDirectory: files, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.World.Canaries/editor-grid/fixture.puck",
            edit: definition => definition with {
                RenderRaw = new WorldRenderDefaults(Indirect: authored,
                LowRaw: preset with { Indirect = (explicitOverrides ? SdfIndirectTier.High : null) },
                MediumRaw: preset,
                HighRaw: preset with { Indirect = (explicitOverrides ? SdfIndirectTier.Off : null) }),
            });
        var host = files.Own(owner: builder.Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var settings = new WorldRenderSettings(defaults: server.Definition.Render);
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new Audio();
        var bindingBar = new WorldBindingBarVisibility();
        var sink = new Sink(sink: WorldSessionLevers.Compose(audio: audio, bindingBar: bindingBar, pacing: pacing, settings: settings));
        using var lease = server.AttachSink(sink: sink);

        Assert.Equal(SdfIndirectTier.Off, settings.IndirectTier);
        foreach (var (name, expected) in new[] {
            ("low", (explicitOverrides ? SdfIndirectTier.High : SdfIndirectTier.Off)),
            ("medium", SdfIndirectTier.Medium),
            ("high", (explicitOverrides ? SdfIndirectTier.Off : SdfIndirectTier.High)),
        }) {
            sink.Delivered.Clear();
            var result = registry.Submit(line: $"world.quality {name}");

            Assert.False(condition: result.IsError, userMessage: result.Output);
            var delivered = Assert.Single(collection: sink.Delivered);

            Assert.Equal(((double)expected), delivered.A);
            Assert.Equal(expected, settings.IndirectTier);
            var saved = WorldSessionLevers.Fold(server.Definition, settings, pacing, audio, bindingBar, new WorldEditorSeats());

            Assert.Equal(expected, saved.Render.Indirect!.Tier);
            Assert.Equal(authored with { Tier = expected }, saved.Render.Indirect);
            Assert.True(condition: WorldDefinitionLoader.TryLoad(WorldDefinitionSerialization.Serialize(definition: saved),
                "indirect-quality.world.json", out var loaded, out var reason), userMessage: reason);
            Assert.NotNull(@object: loaded);
            Assert.Equal(expected, new WorldRenderSettings(defaults: loaded.Render).IndirectTier);
        }
        Assert.Same(authored, server.Definition.Render.Indirect);
    }
    [InlineData(-1)]
    [InlineData(3)]
    [Theory]
    public void UnknownBootAndPresetTiersRefuseBeforePresentation(int ordinal) {
        var invalid = ((SdfIndirectTier)ordinal);
        WorldRenderDefaults[] rows = [
            new(Indirect: new(Tier: invalid)),
            new(LowRaw: new(ShadowTier.Off, false, 1f, Indirect: invalid)),
        ];

        foreach (var row in rows) {
            Assert.False(condition: WorldDefinitionValidator.TryValidate(Fixtures.BuildDocument() with { RenderRaw = row },
                neighbours: null, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "indirect");
        }
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
    private sealed class Sink(WorldSessionLeverSink sink) : IClientSink {
        public List<WorldSessionLever> Delivered { get; } = [];

        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) { }
        public void DeliverSnapshot(in WorldSnapshot snapshot) { }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) { }
        public void DeliverSessionLever(WorldSessionLever lever) {
            if (lever.Name != WorldSessionLevers.Indirect) { return; }
            Delivered.Add(item: lever);
            Assert.True(condition: sink.TryApply(lever: lever));
        }
    }
}
