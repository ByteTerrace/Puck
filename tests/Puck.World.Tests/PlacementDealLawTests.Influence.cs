using Puck.Commands;
using System.Numerics;
using Puck.Abstractions.Counting;
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
            channel: "water",
            key: "a",
            placementId: TemplateId
        );
        var second = new PlacementInfluenceOperand(
            channel: "water",
            key: "b",
            placementId: TemplateId
        );

        Assert.Equal(
            1,
            fixture.Server.RuleHost.Read(operand: first).Value
        );
        Assert.Equal(
            0,
            fixture.Server.RuleHost.Read(operand: second).Value
        );
        Assert.True(condition: fixture.Server.RuleHost.Read(operand: new PlacementInfluenceOperand(
            channel: "water",
            key: "missing",
            placementId: TemplateId
        )).IsAbsent);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            supply with {
                Spatial = [Water(
                    name: "outer",
                    radius: 3
                )],
            }
        ));
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.RuleHost.Read(operand: second).Value
        );
        Assert.Equal(
            0,
            fixture.Server.RuleHost.Read(operand: new PlacementInfluenceOperand(
                channel: "power",
                key: "b",
                placementId: TemplateId
            )).Value
        );

        Assert.Equal(
            0L,
            AllocationWindow.Least(window: () => {
                for (var read = 0; (read < 100); read++) { _ = fixture.Server.RuleHost.Read(operand: second); }
            })
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
