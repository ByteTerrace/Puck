using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The independent field scale reaches live cameras and authored presets without changing scene resolution.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldSkyFieldScaleLawTests {
    [Fact]
    public void CommandPresetFrameAndSavedBootKeepTheSameFieldFraction() {
        using var files = new TemporaryDirectory(prefix: "sky-field-scale-");
        var builder = WorldBootHarness.Compose(stateDirectory: files, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.Counters/counters.puck",
            edit: definition => definition with {
                RenderRaw = definition.Render with {
                    SkyFieldScale = 1f,
                    LowRaw = new(ShadowTier.Off, false, 1f, SkyFieldScale: .5f),
                    MediumRaw = new(ShadowTier.Off, false, 1f, SkyFieldScale: 1f),
                },
            });
        var host = files.Own(owner: builder.Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var pacing = new PresentPacingControl(initialTargetHertz: null);
        var audio = new Audio();
        var bindingBar = new WorldBindingBarVisibility();
        var sink = new Sink(sink: WorldSessionLevers.Compose(audio: audio, bindingBar: bindingBar, pacing: pacing, settings: settings));
        using var lease = server.AttachSink(sink: sink);

        var sceneScale = settings.RenderScale;
        var revision = settings.Revision;

        Assert.Equal("[world.sky-field-scale: 1]", registry.Submit(line: "world.sky-field-scale").Output);
        Assert.Equal(revision, settings.Revision);
        foreach (var (command, fraction) in new[] {
            ("world.sky-field-scale 0.5", .5f), ("world.sky-field-scale 1", 1f),
            ("world.quality low", .5f), ("world.quality medium", 1f),
        }) {
            var result = registry.Submit(line: command);

            Assert.False(condition: result.IsError, userMessage: result.Output);
            Assert.Equal(fraction, settings.SkyFieldScale);
            Assert.Equal(sceneScale, settings.RenderScale);
            var frame = presenter.CaptureFrame(deltaSeconds: 0f, height: 65, interpolationAlpha: 1f, width: 97);

            Assert.NotEmpty(collection: frame.Views);
            Assert.All(frame.Views, view => Assert.Equal(fraction, view.Quality.SkyFieldFraction));
            var panel = presenter.DressResolution(new SdfViewSnapshot { Quality = WorldSessionSceneEmitter.ReducedQuality },
                WorldViewGraphs.WorldInstance, width: 97, height: 65);

            Assert.Equal(fraction, panel.Quality.SkyFieldFraction);
            var saved = WorldSessionLevers.Fold(server.Definition, settings, pacing, audio, bindingBar, new WorldEditorSeats());

            Assert.Equal(fraction, saved.Render.SkyFieldScale);
            Assert.True(condition: WorldDefinitionLoader.TryLoad(WorldDefinitionSerialization.Serialize(definition: saved),
                "sky-field-scale.world.json", out var loaded, out var reason), userMessage: reason);
            Assert.NotNull(@object: loaded);
            Assert.Equal(fraction, new WorldRenderSettings(defaults: loaded.Render).SkyFieldScale);
        }
        foreach (var command in new[] { "world.sky-field-scale 0", "world.sky-field-scale 0.75",
            "world.sky-field-scale NaN", "world.sky-field-scale 0.5 1" }) {
            revision = settings.Revision;
            Assert.True(condition: registry.Submit(line: command).IsError);
            Assert.Equal(revision, settings.Revision);
        }
        Assert.Equal(1f, server.Definition.Render.SkyFieldScale);
    }
    [InlineData(0f)]
    [InlineData(.75f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void InvalidBootPresetAndLiveFractionsRefuse(float fraction) {
        foreach (var render in new WorldRenderDefaults[] {
            new(SkyFieldScale: fraction), new(LowRaw: new(ShadowTier.Off, false, 1f, SkyFieldScale: fraction)),
        }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidate(Fixtures.BuildDocument() with { RenderRaw = render },
                neighbours: null, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "skyFieldScale");
        }
        var settings = new WorldRenderSettings(defaults: WorldRenderDefaults.Absent);

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => settings.SkyFieldScale = fraction);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => _ = new SdfViewQuality { SkyFieldScale = ((fraction == 0f) ? -1f : fraction) }.SkyFieldFraction);
        Assert.Equal(1f, settings.SkyFieldScale);
    }
    [Fact]
    public void HalfFieldRestrictionAndAuthoredDslSurviveComposition() {
        var half = new SdfViewQuality { SkyFieldScale = .5f };

        Assert.Equal(.5f, half.Restrict(other: default).SkyFieldFraction);
        Assert.Equal(.5f, default(SdfViewQuality).Restrict(other: half).SkyFieldFraction);
        Assert.Equal(1f, default(SdfViewQuality).SkyFieldFraction);
        using var files = new TemporaryDirectory(prefix: "sky-field-dsl-");
        var source = Path.Combine(path1: files.RootPath, path2: "field.puck");

        File.WriteAllText(contents: """
            schema: "puck.world.definition.v1"
            documentId: "field"
            render {
              skyFieldScale: 0.5
              low { shadows: Off ambientOcclusion: false renderScale: 1 skyFieldScale: 0.5 }
            }
            """, path: source);
        var compilation = WorldCompiler.CompileFile(path: source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport(sourceText: File.ReadAllText(path: source), filePath: source));
        Assert.Equal(.5m, compilation.RequireJson()["render"]!["skyFieldScale"]!.GetValue<decimal>());
        Assert.Equal(.5m, compilation.RequireJson()["render"]!["low"]!["skyFieldScale"]!.GetValue<decimal>());
    }

    private sealed class Audio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
    private sealed class Sink(WorldSessionLeverSink sink) : IClientSink {
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) { }
        public void DeliverSnapshot(in WorldSnapshot snapshot) { }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) { }
        public void DeliverSessionLever(WorldSessionLever lever) => Assert.True(condition: sink.TryApply(lever: lever));
    }
}
