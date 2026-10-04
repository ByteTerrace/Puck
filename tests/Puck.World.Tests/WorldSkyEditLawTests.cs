using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Commands;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldSkyEditLawTests {
    internal sealed class Session : IDisposable {
        private readonly TemporaryDirectory m_files = new();
        private readonly WorldEnvironmentResolve m_environment = new(new WorldValueDomainGuard());

        private readonly InputRouter m_router;
        private readonly string m_live;
        private readonly string m_original;

        public readonly WorldServer Server;
        public readonly WorldClient Client;
        public readonly CommandRegistry Commands;
        public readonly WorldCompareCapture Capture;
        public readonly WorldFrameComparison Comparison;
        public readonly WorldSeatViewports Viewports;
        public readonly string SavedPath;

        public Session() {
            var host = m_files.Own(WorldBootHarness.Compose(m_files, WorldHostPresentation.Offscreen,
                "tests/Puck.World.Canaries/editor-grid/fixture.world.json", definition => definition with {
                    RenderRaw = definition.Render with { Sky = Sky("#FF0000"), Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.01f)) },
                    TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 1d)]),
                }).Build());

            Assert.True(WorldPostBuildWiring.Install(host.Services));
            Server = host.Services.GetRequiredService<WorldServer>();
            Client = host.Services.GetRequiredService<WorldClient>();
            Commands = host.Services.GetRequiredService<CommandRegistry>();
            Capture = host.Services.GetRequiredService<WorldCompareCapture>();
            Comparison = host.Services.GetRequiredService<WorldFrameComparison>();
            Viewports = host.Services.GetRequiredService<WorldSeatViewports>();
            m_router = host.Services.GetRequiredService<InputRouter>();
            m_live = m_files.PathOf("p18-12-live.puck");
            SavedPath = m_files.PathOf("p18-12-saved.puck");
            m_original = Text(Server.Definition);
            File.WriteAllText(m_live, m_original);
            File.WriteAllText(SavedPath, m_original);
            host.Services.GetRequiredService<WorldDefinitionSource>().SourcePath = m_live;
        }

        private string Text(WorldDefinition definition) => (("// Retain the sky artist's heading.\n" +
            WorldDecompiler.Decompile(JsonNode.Parse(WorldDefinitionSerialization.SerializeBeside(definition, m_live))!.AsObject())) +
            "\n// Retain this unrelated constant.\nlet retained = 3\n");

        public WorldResolvedEnvironment Resolve() => m_environment.Resolve(Client.Definition, Client.DefinitionRevision, Client.StateMirror);
        public void EditAndReload() {
            var edited = Server.Definition with { RenderRaw = Server.Definition.Render with { Sky = Sky("#0000FF"), Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.03f)) } };

            File.WriteAllText(m_live, Text(edited));
            var result = Commands.Submit("world.reload");

            Assert.False(result.IsError, result.Output);
            Commands.ApplySnapshot(m_router.SnapshotForTick(Server.NextInputTick, ulong.MaxValue));
            Server.Advance(Fixtures.StepTicks);
        }
        public void SaveAndAssert() {
            var result = Commands.Submit(("world.save " + SavedPath.Replace('\\', '/')));

            Assert.False(result.IsError, result.Output);
            var saved = File.ReadAllText(SavedPath);

            Assert.StartsWith("// Retain the sky artist's heading.\n", saved);
            Assert.EndsWith("\n// Retain this unrelated constant.\nlet retained = 3\n", saved);
            var compiled = WorldCompiler.Compile(saved, sourcePath: SavedPath);
            var sky = compiled.RequireJson()["render"]!["sky"];
            var live = JsonNode.Parse(WorldDefinitionSerialization.Serialize(Server.Definition))!["render"]!["sky"];

            Assert.True(JsonNode.DeepEquals(live, sky), "The saved .puck sky rows must reproduce the edited live sky.");
            Assert.Equal(0.03f, compiled.RequireJson()["render"]!["atmosphere"]!["fog"]!["density"]!.GetValue<float>());
            Assert.DoesNotContain("#FF0000", saved);
            Assert.Contains("#0000FF", saved);
        }
        public void Dispose() { m_environment.Dispose(); m_files.Dispose(); }

        private static WorldRenderSky Sky(string color) => new(Layers: [
            new WorldRenderSkyLayer.Gradient(Name: "horizon", Stops: [
                new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(color)),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(color)),
            ]),
            new WorldRenderSkyLayer.Stars(Name: "stars", Brightness: 0.2f),
            new WorldRenderSkyLayer.Clouds(Name: "clouds", Coverage: 0.2f),
        ]);
    }

    private sealed class Target : ICaptureRequestTarget {
        public string? PendingCapturePath => Request?.Path;
        public FrameCaptureRequest? Request { get; private set; }

        public void RequestCapture(FrameCaptureRequest request) => Request = request;
    }

    [Fact]
    public void SkyEditReloadsComparesAndSavesItsPuckRowsThroughTheExistingCommands() {
        using var session = new Session();
        var target = new Target();
        var completed = 0UL;

        session.Capture.Attach(() => target, () => completed);
        session.Viewports.Publish(0, new NormalizedRect(0, 0, 1, 1), default, SdfSkyEnvironment.Size, SdfSkyEnvironment.Size);
        void Compare(string command) {
            var image = ReferenceMap(session.Resolve());

            session.Capture.RecordPreparedFrame();
            var result = session.Commands.Submit(command);

            Assert.False(result.IsError, result.Output);
            _ = target.Request!.Write(path => PngEncoder.Write(path, image.RgbaPixels, image.Width, image.Height), tick: session.Server.CompletedEngineTicks, frame: ++completed);
            session.Capture.Poll();
            Assert.Null(target.Request.Completion.Result.Error);
        }
        Compare("world.compare hold");
        session.EditAndReload();
        Compare("world.compare diff");
        Assert.Equal(SdfSkyEnvironment.Texels, session.Comparison.Seat(0)!.Difference!.Value.ChangedPixels);
        Assert.Equal(0.03f, session.Resolve().Sky.Atmosphere.FogDensity);
        session.SaveAndAssert();
    }

    private static PngImage ReferenceMap(WorldResolvedEnvironment environment) {
        var sky = environment.Sky;
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(lights: environment.Lights, block: out var block, layers: layers, details: new SdfSkyDetails(), farDistance: 40f);
        var map = new Vector3[SdfSkyEnvironment.Texels];

        SdfSkyEnvironment.Render(in block, layers, map);
        var pixels = new byte[(map.Length * 4)];

        for (var index = 0; (index < map.Length); index++) {
            pixels[(index * 4)] = ((byte)Math.Clamp((map[index].X * 255f), 0f, 255f));
            pixels[((index * 4) + 1)] = ((byte)Math.Clamp((map[index].Y * 255f), 0f, 255f));
            pixels[((index * 4) + 2)] = ((byte)Math.Clamp((map[index].Z * 255f), 0f, 255f));
            pixels[((index * 4) + 3)] = 255;
        }
        return new PngImage(Width: SdfSkyEnvironment.Size, Height: SdfSkyEnvironment.Size, RgbaPixels: pixels);
    }
}
