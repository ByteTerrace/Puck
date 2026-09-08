using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public void InfluenceCountsProvidersAndTracksExpansionThroughOrdinaryPlacementEdits() {
        var template = Template(new WorldPlacementDeal(RowName)) with {
            Spatial = [new WorldPlacementSpatialVolume("store", WorldPlacementSpatialRole.Occupation,
                new WorldSpatialShape(WorldSpatialShapeKind.Box, Vector3.Zero, new Vector3(.4f)))],
        };
        var supply = new WorldPlacement("well", StoreCreation, new Vector3(10, 0, 10), 0, 1,
            Spatial: [Water("inner", .75f), Water("outer", 1f)]);
        var document = Document(template, AccountsRow("a", "b")) with { PlacementRowsRaw = [template, supply] };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        var first = new PlacementInfluenceOperand("water", TemplateId, "a");
        var second = new PlacementInfluenceOperand("water", TemplateId, "b");
        Assert.Equal(1, first.Read(fixture.Server).Value);
        Assert.Equal(0, second.Read(fixture.Server).Value);
        Assert.True(new PlacementInfluenceOperand("water", TemplateId, "missing").Read(fixture.Server).IsAbsent);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(WorldPrincipal.Console,
            supply with { Spatial = [Water("outer", 3)] }));
        fixture.Step();
        Assert.Equal(1, second.Read(fixture.Server).Value);
        Assert.Equal(0, new PlacementInfluenceOperand("power", TemplateId, "b").Read(fixture.Server).Value);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var read = 0; read < 100; read++) { _ = second.Read(fixture.Server); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ModuleInfluenceLabelsStaySharedEvenWhenALocalRowUsesTheSameName() {
        var declared = new Dictionary<string, string> { ["water"] = "court_water", ["stores"] = "court_stores" };
        Assert.Equal("$influence:water:court_stores",
            WorldModuleNamespace.Rewrite("$influence:water:stores", WorldNameRole.Names, declared));
    }

    private static WorldPlacementSpatialVolume Water(string name, float radius) => new(name,
        WorldPlacementSpatialRole.Influence,
        new WorldSpatialShape(WorldSpatialShapeKind.Sphere, Vector3.Zero, Vector3.Zero, Radius: radius), Channel: "water");
}
