using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The real reload command retains the builder's seat state and drops only ids absent from its accepted document.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldEditorReloadRetentionLawTests {
    [Fact]
    public void WatchCommandSubmitsAndSettlesTheOrdinaryReload() {
        using var files = new TemporaryDirectory();
        var host = files.Own(owner: WorldBootHarness.Compose(files, WorldHostPresentation.None, "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var server = host.Services.GetRequiredService<WorldServer>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var router = host.Services.GetRequiredService<InputRouter>();
        var watch = host.Services.GetRequiredService<WorldSourceWatch>();
        var text = host.Services.GetRequiredService<TextCommandSource>();
        var source = host.Services.GetRequiredService<WorldDefinitionSource>();
        var path = files.PathOf(name: "watched.world.json");
        var bytes = WorldDefinitionSerialization.Serialize(definition: server.Definition);

        File.WriteAllBytes(bytes: bytes, path: path);
        source.SourcePath = path;
        Assert.False(condition: registry.Submit(line: "world.watch on").IsError);
        Assert.True(condition: watch.Enabled);
        File.AppendAllText(contents: "\n ", path: path);
        watch.Poll(now: System.Diagnostics.Stopwatch.Frequency);
        watch.Poll(now: (System.Diagnostics.Stopwatch.Frequency * 2));
        Assert.Equal(1, watch.ReloadCount);
        text.Collect();
        registry.ApplySnapshot(snapshot: router.SnapshotForTick(tick: server.NextInputTick, windowEndTick: ulong.MaxValue));
        server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Null(@object: watch.LastError);
        File.AppendAllText(contents: "\n  ", path: path);
        watch.Poll(now: (System.Diagnostics.Stopwatch.Frequency * 3));
        watch.Poll(now: (System.Diagnostics.Stopwatch.Frequency * 4));
        Assert.Equal(2, watch.ReloadCount);
    }
    [Fact]
    public void ReloadRetainsSeatStateAndClearsRemovedSelections() {
        using var files = new TemporaryDirectory();
        var host = files.Own(owner: WorldBootHarness.Compose(files, WorldHostPresentation.None, "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var server = host.Services.GetRequiredService<WorldServer>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var router = host.Services.GetRequiredService<InputRouter>();
        var seats = host.Services.GetRequiredService<WorldEditorSeats>();
        var bindings = host.Services.GetRequiredService<WorldSeatBindings>();
        var view = client.Roster.Seat(slot: 0)!.View;
        var source = host.Services.GetRequiredService<WorldDefinitionSource>();
        var original = server.Definition;
        var id = original.Placements.First().Id;
        var path = files.PathOf(name: "reload.world.json");

        source.SourcePath = path;
        bindings.SetContextState(family: WorldContextFamilies.Editor, slot: 0, state: WorldContextFamilies.EditorBuild);
        seats.SetGridPitch(0, new Vector3(value: 2f));
        seats.SetSnapEnabled(enabled: true, slot: 0);
        seats.SetCurrent(placement: id, slot: 0, world: "boot");
        seats.SetReference(placement: id, slot: 0, world: "boot");
        seats.SetCurrent(placement: id, slot: 1, world: "another-world");
        view.Nudge(new Vector2(x: 1f, y: .5f), .2f, .1f, original.PlayerDefaults.SeatLook, original.Views);
        var angles = (view.Yaw, view.Pitch);

        void Reload(WorldDefinition definition) {
            File.WriteAllBytes(path, WorldDefinitionSerialization.Serialize(definition: definition));
            var submitted = registry.Submit(line: "world.reload");

            Assert.False(condition: submitted.IsError, userMessage: submitted.Output);
            registry.ApplySnapshot(snapshot: router.SnapshotForTick(tick: server.NextInputTick, windowEndTick: ulong.MaxValue));
            server.Advance(stepTicks: Fixtures.StepTicks);
        }

        Reload(definition: original);
        Assert.Equal(id, seats.CurrentOf(slot: 0, world: "boot"));
        Assert.Equal(id, seats.ReferenceOf(slot: 0, world: "boot"));
        Assert.True(condition: bindings.IsBuilding(slot: 0));
        Assert.Equal(new Vector3(value: 2f), ((Vector3)seats.GridOf(0, server.Definition.Editor).ResolvedPitch));
        Assert.True(condition: seats.SnapOf(0, server.Definition.Editor).Enabled);
        Assert.Same(view, client.Roster.Seat(slot: 0)!.View);
        Assert.Equal(angles, (view.Yaw, view.Pitch));
        Reload(definition: original with { PlacementsRaw = original.PlacementsRaw! with { Rows = [] }, PlacementRowsRaw = [] });
        Assert.Empty(collection: server.Definition.Placements);
        Assert.Null(@object: seats.CurrentOf(slot: 0, world: "boot"));
        Assert.Null(@object: seats.ReferenceOf(slot: 0, world: "boot"));
        Assert.Equal(id, seats.CurrentOf(slot: 1, world: "another-world"));
        Assert.True(condition: bindings.IsBuilding(slot: 0));
        Assert.Equal(angles, (view.Yaw, view.Pitch));
    }
}
