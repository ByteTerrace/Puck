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
}
