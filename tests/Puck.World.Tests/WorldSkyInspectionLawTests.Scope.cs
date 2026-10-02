using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSkyInspectionLawTests {
    private static WorldDefinition WithInspectionLayers(WorldDefinition definition, params string[] names) => definition with {
        RenderRaw = definition.Render with { Sky = new(Layers: names.Select(selector: static name => ((WorldRenderSkyLayer)new WorldRenderSkyLayer.Pattern { Name = name })).ToArray()) },
    };

    [Fact]
    public void WorldSelectionsSurviveNewDefinitionsAndPruneRemovedNamesOnceWithoutFolding() {
        var definition = WithInspectionLayers(Fixtures.BuildDocument(), "stars", "clouds");
        var reports = new List<string>();
        var seats = new WorldEditorSeats { SkyReport = reports.Add };

        seats.SetSkyMute(definition: definition, layer: "clouds", muted: true, slot: 0, world: "A");
        seats.SetSkySolo(definition: definition, layer: "stars", slot: 0, world: "A");
        var selected = seats.SkyOf(definition: definition, slot: 0, world: "A");

        Assert.Same(SdfSkyInspection.None, seats.SkyOf(definition: definition, slot: 0, world: "B"));
        Assert.Same(SdfSkyInspection.None, seats.SkyOf(definition: definition, slot: 1, world: "A"));
        Assert.Same(selected, seats.SkyOf(definition: definition with { }, slot: 0, world: "A"));
        Assert.Null(@object: seats.Fold(authored: null));
        Assert.Empty(collection: reports);
        Assert.Equal(0L, AllocationWindow.Least(() => {
            for (var index = 0; (index < 100); index++) { _ = seats.SkyOf(definition: definition, slot: 0, world: "A"); }
        }));
        var changed = WithInspectionLayers(definition, "clouds");
        var pruned = seats.SkyOf(definition: changed, slot: 0, world: "A");

        Assert.Null(@object: pruned.Solo);
        Assert.False(condition: pruned.Allows(layer: "clouds"));
        Assert.Contains("stars", Assert.Single(collection: reports));
        Assert.Same(pruned, seats.SkyOf(definition: changed, slot: 0, world: "A"));
        Assert.Single(collection: reports);
        Assert.True(condition: selected.Allows(layer: "stars"));
    }
    [Fact]
    public void DottedCommandsRetainMutesAcrossSoloAndRealReloadAndNameUnknownLayers() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.None,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json",
            edit: definition => WithInspectionLayers(definition, "stars", "clouds")).Build();

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var server = host.Services.GetRequiredService<WorldServer>();
        var seats = host.Services.GetRequiredService<WorldEditorSeats>();
        var input = host.Services.GetRequiredService<InputRouter>();
        var reports = new List<string>();

        seats.SkyReport = reports.Add;
        Assert.False(condition: registry.Submit(line: "world.sky.mute clouds on").IsError);
        Assert.False(condition: registry.Submit(line: "world.sky.solo clouds").IsError);
        Assert.True(condition: seats.SkyOf(0, "boot", server.Definition).Allows(layer: "clouds"));
        var unknown = registry.Submit(line: "world.sky.solo absent");

        Assert.True(condition: unknown.IsError);
        Assert.Contains("stars", unknown.Output);
        Assert.Contains("clouds", unknown.Output);
        Assert.Contains("muted=clouds", registry.Submit(line: "world.sky.mute").Output);
        Assert.Contains("solo=clouds", registry.Submit(line: "world.sky.solo").Output);
        Assert.False(condition: registry.Submit(line: "world.sky.solo off").IsError);
        Assert.False(condition: seats.SkyOf(0, "boot", server.Definition).Allows(layer: "clouds"));
        var original = server.Definition;
        var path = files.PathOf(name: "sky-reload.world.json");

        host.Services.GetRequiredService<WorldDefinitionSource>().SourcePath = path;
        void Reload(WorldDefinition next) {
            File.WriteAllBytes(path, WorldDefinitionSerialization.Serialize(definition: next));
            var result = registry.Submit(line: "world.reload");

            Assert.False(condition: result.IsError, userMessage: result.Output);
            registry.ApplySnapshot(snapshot: input.SnapshotForTick(tick: server.NextInputTick, windowEndTick: ulong.MaxValue));
            server.Advance(stepTicks: Fixtures.StepTicks);
            Assert.False(condition: registry.Submit(line: "world.sky.mute").IsError);
        }
        Reload(next: original);
        Assert.False(condition: seats.SkyOf(0, "boot", server.Definition).Allows(layer: "clouds"));
        Assert.Empty(collection: reports);
        Reload(next: WithInspectionLayers(original, "stars"));
        Assert.True(condition: seats.SkyOf(0, "boot", server.Definition).Allows(layer: "clouds"));
        Assert.Contains("clouds", Assert.Single(collection: reports));
    }
}
public sealed partial class WorldRoutedPresentationLawTests {
    [Fact]
    public void SkySelectionsFollowAddressedWorldAndWindowOwnerWithoutCrossingWorldOrSeat() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build();

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var editor = host.Services.GetRequiredService<WorldEditorSeats>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();
        var definitionA = AwayDocument() with { RenderRaw = new(Sky: new(Layers: [new WorldRenderSkyLayer.Pattern { Name = "stars" }, new WorldRenderSkyLayer.Pattern { Name = "clouds" }])) };
        var definitionB = AwayDocument() with { RenderRaw = new(Sky: new(Layers: [new WorldRenderSkyLayer.Pattern { Name = "paint" }])) };
        using var a = Endpoint(definition: definitionA, identity: "inspection-a", position: AwayPose);
        using var b = Endpoint(definition: definitionB, identity: "inspection-b", position: AwayPose);

        _ = routes.Publish(endpoint: a, entity: a.Mirror.Address(index: 0), slot: 0);
        var result = registry.Submit(line: "world.sky.solo stars");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Equal("stars", editor.SkyOf(definition: definitionA, slot: 0, world: "inspection-a").Solo);
        using var a0 = presenter.AttachWindow(a, slot: 0);
        using var a1 = presenter.AttachWindow(a, slot: 1);
        using var b0 = presenter.AttachWindow(b, slot: 0);

        _ = Capture(source: presenter);
        var aFrame = Capture(source: a0.Scene.FrameSource);
        var bFrame = Capture(source: b0.Scene.FrameSource);

        Assert.Equal("stars", aFrame.Views[a0.Index].SkyInspection?.Solo);
        Assert.False(condition: aFrame.Views[a0.Index].SkyInspection!.Allows(layer: "clouds"));
        Assert.Null(@object: aFrame.Views[a1.Index].SkyInspection?.Solo);
        Assert.True(condition: bFrame.Views[b0.Index].SkyInspection!.Allows(layer: "paint"));
        Assert.Null(@object: bFrame.Views[b0.Index].SkyInspection?.Solo);
        Assert.Contains("inspection-a", registry.Submit(line: "world.sky.solo").Output);
    }
}
