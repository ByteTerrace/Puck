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

[Collection(SceneProbeCollection.Name)]
public sealed class WorldSkyEditLawTests {
    internal sealed class Session : IDisposable {
        private readonly TemporaryDirectory m_files = new();
        private readonly WorldEnvironmentResolve m_environment = new(domains: new WorldValueDomainGuard());

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
            var host = m_files.Own(owner: WorldBootHarness.Compose(m_files, WorldHostPresentation.Offscreen,
                "tests/Puck.World.Canaries/editor-grid/fixture.world.json", definition => definition with {
                    RenderRaw = definition.Render with { Sky = Sky(color: "#FF0000"), Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.01f)) },
                    TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 1d)]),
                }).Build());

            Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
            Server = host.Services.GetRequiredService<WorldServer>();
            Client = host.Services.GetRequiredService<WorldClient>();
            Commands = host.Services.GetRequiredService<CommandRegistry>();
            Capture = host.Services.GetRequiredService<WorldCompareCapture>();
            Comparison = host.Services.GetRequiredService<WorldFrameComparison>();
            Viewports = host.Services.GetRequiredService<WorldSeatViewports>();
            m_router = host.Services.GetRequiredService<InputRouter>();
            m_live = m_files.PathOf(name: "p18-12-live.puck");
            SavedPath = m_files.PathOf(name: "p18-12-saved.puck");
            m_original = Text(definition: Server.Definition);
            File.WriteAllText(contents: m_original, path: m_live);
            File.WriteAllText(contents: m_original, path: SavedPath);
            host.Services.GetRequiredService<WorldDefinitionSource>().SourcePath = m_live;
        }

        private string Text(WorldDefinition definition) => (("// Retain the sky artist's heading.\n" +
            WorldDecompiler.Decompile(JsonNode.Parse(WorldDefinitionSerialization.SerializeBeside(definition: definition, path: m_live))!.AsObject())) +
            "\n// Retain this unrelated constant.\nlet retained = 3\n");

        public WorldResolvedEnvironment Resolve() => m_environment.Resolve(Client.Definition, Client.DefinitionRevision, Client.StateMirror);
        public void EditAndReload() {
            var edited = Server.Definition with { RenderRaw = Server.Definition.Render with { Sky = Sky(color: "#0000FF"), Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.03f)) } };

            File.WriteAllText(m_live, Text(definition: edited));
            var result = Commands.Submit(line: "world.reload");

            Assert.False(condition: result.IsError, userMessage: result.Output);
            Commands.ApplySnapshot(snapshot: m_router.SnapshotForTick(tick: Server.NextInputTick, windowEndTick: ulong.MaxValue));
            Server.Advance(stepTicks: Fixtures.StepTicks);
        }
        public void SaveAndAssert() {
            var result = Commands.Submit(line: ("world.save " + SavedPath.Replace(newChar: '/', oldChar: '\\')));

            Assert.False(condition: result.IsError, userMessage: result.Output);
            var saved = File.ReadAllText(path: SavedPath);

            Assert.StartsWith(actualString: saved, expectedStartString: "// Retain the sky artist's heading.\n");
            Assert.EndsWith(actualString: saved, expectedEndString: "\n// Retain this unrelated constant.\nlet retained = 3\n");
            var compiled = WorldCompiler.Compile(saved, sourcePath: SavedPath);
            var sky = compiled.RequireJson()["render"]!["sky"];
            var savedDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: compiled.RequireJson().ToJsonString()));
            var live = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: Server.Definition))!["render"]!["sky"];

            Assert.True(condition: JsonNode.DeepEquals(node1: live, node2: sky), userMessage: "The saved .puck sky rows must reproduce the edited live sky.");
            Assert.Equal(expected: new BindableScalar(literal: 0.03f), actual: savedDefinition.Render.Atmosphere!.Fog!.Density);
            Assert.DoesNotContain(actualString: saved, expectedSubstring: "#FF0000");
            Assert.Contains(actualString: saved, expectedSubstring: "#0000FF");
        }
        public void Dispose() { m_environment.Dispose(); m_files.Dispose(); }

        private static WorldRenderSky Sky(string color) => new(Layers: [
            new WorldRenderSkyLayer.Gradient(Name: "horizon", Stops: [
                new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: color)),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: color)),
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

        session.Capture.Attach(completedFrames: () => completed, target: () => target);
        session.Viewports.Publish(0, new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0), default, SdfSkyEnvironment.Size, SdfSkyEnvironment.Size);
        void Compare(string command) {
            var image = ReferenceMap(environment: session.Resolve());

            session.Capture.RecordPreparedFrame();
            var result = session.Commands.Submit(line: command);

            Assert.False(condition: result.IsError, userMessage: result.Output);
            _ = target.Request!.Write(path => PngEncoder.Write(path, image.RgbaPixels, image.Width, image.Height), tick: session.Server.CompletedEngineTicks, frame: ++completed);
            session.Capture.Poll();
            Assert.Null(@object: target.Request.Completion.Result.Error);
        }
        Compare(command: "world.compare hold");
        session.EditAndReload();
        Compare(command: "world.compare diff");
        Assert.Equal(SdfSkyEnvironment.Texels, session.Comparison.Seat(slot: 0)!.Difference!.Value.ChangedPixels);
        Assert.Equal(0.03f, session.Resolve().Sky.Atmosphere.FogDensity);
        session.SaveAndAssert();
    }

    private static PngImage ReferenceMap(WorldResolvedEnvironment environment) {
        var sky = environment.Sky;
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(lights: environment.Lights, block: out var block, layers: layers, details: new SdfSkyDetails(), farDistance: 40f);
        var map = new Vector3[SdfSkyEnvironment.Texels];

        SdfSkyEnvironment.Render(block: in block, layers: layers, map: map);
        var pixels = new byte[(map.Length * 4)];

        for (var index = 0; (index < map.Length); index++) {
            pixels[(index * 4)] = ((byte)Math.Clamp((map[index].X * 255f), 0f, 255f));
            pixels[((index * 4) + 1)] = ((byte)Math.Clamp((map[index].Y * 255f), 0f, 255f));
            pixels[((index * 4) + 2)] = ((byte)Math.Clamp((map[index].Z * 255f), 0f, 255f));
            pixels[((index * 4) + 3)] = 255;
        }
        return new PngImage(Height: SdfSkyEnvironment.Size, RgbaPixels: pixels, Width: SdfSkyEnvironment.Size);
    }
}
