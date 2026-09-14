using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PlacementDealLawTests {
    [Fact]
    public void InfluenceCountsProvidersAndTracksExpansionThroughOrdinaryPlacementEdits() {
        var template = Template(new WorldPlacementDeal(RowName)) with {
            Spatial = [new WorldPlacementSpatialVolume(
                "store",
                WorldPlacementSpatialRole.Occupation,
                new WorldSpatialShape(
                    WorldSpatialShapeKind.Box,
                    Vector3.Zero,
                    new Vector3(value: .4f)
                )
            )],
        };
        var supply = new WorldPlacement(
            "well",
            StoreCreation,
            new Vector3(
                x: 10,
                y: 0,
                z: 10
            ),
            0,
            1,
            Spatial: [Water(
                    name: "inner",
                    radius: .75f
                ), Water(
                    name: "outer",
                    radius: 1f
                )]
        );
        var document = Document(
            template,
            AccountsRow(
                "a",
                "b"
            )
        ) with { PlacementRowsRaw = [template, supply] };
        using var fixture = Fixtures.FreshServer(document);

        fixture.Step();
        var first = new PlacementInfluenceOperand(
            "water",
            TemplateId,
            "a"
        );
        var second = new PlacementInfluenceOperand(
            "water",
            TemplateId,
            "b"
        );

        Assert.Equal(
            1,
            first.Read(reader: fixture.Server).Value
        );
        Assert.Equal(
            0,
            second.Read(reader: fixture.Server).Value
        );
        Assert.True(condition: new PlacementInfluenceOperand(
            "water",
            TemplateId,
            "missing"
        ).Read(reader: fixture.Server).IsAbsent);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            WorldPrincipal.Console,
            supply with { Spatial = [Water(
                    name: "outer",
                    radius: 3
                )] }
        ));
        fixture.Step();
        Assert.Equal(
            1,
            second.Read(reader: fixture.Server).Value
        );
        Assert.Equal(
            0,
            new PlacementInfluenceOperand(
                "power",
                TemplateId,
                "b"
            ).Read(reader: fixture.Server).Value
        );

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var read = 0; (read < 100); read++) { _ = second.Read(reader: fixture.Server); }
        Assert.Equal(
            0,
            (GC.GetAllocatedBytesForCurrentThread() - before)
        );
    }
    [Fact]
    public void ModuleInfluenceLabelsStaySharedEvenWhenALocalRowUsesTheSameName() {
        var declared = new Dictionary<string, string> { ["water"] = "court_water", ["stores"] = "court_stores" };

        Assert.Equal(
            "$influence:water:court_stores",
            WorldModuleNamespace.Rewrite(
                declared: declared,
                role: WorldNameRole.Names,
                text: "$influence:water:stores"
            )
        );
    }

    private static WorldPlacementSpatialVolume Water(string name, float radius) => new(
        name,
        WorldPlacementSpatialRole.Influence,
        new WorldSpatialShape(
            WorldSpatialShapeKind.Sphere,
            Vector3.Zero,
            Vector3.Zero,
            Radius: radius
        ),
        Channel: "water"
    );
}
