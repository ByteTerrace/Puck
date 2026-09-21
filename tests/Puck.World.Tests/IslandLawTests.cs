using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the shape of the one shipped world: every district reachable by its arrival spawn and rooted under
/// its court, the plaza's four arches framing their districts through view screens, the split seat layout beside the
/// action layout, a capture station per district, and a radial gravity area under each planetoid.</summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class IslandLawTests {
    private static readonly string[] Arches = ["dive", "kart", "jump", "studio"];
    private static readonly string[] Districts = ["dive", "kart", "jump", "studio", "arena", "arcade", "granaries", "market", "proving", "garden"];

    [Fact]
    public void ACaptureStationPerDistrictSelectsItsCamera() {
        var definition = AuthoredGameFixtures.Nexus;
        var captures = Assert.IsType<WorldCapturesSection>(@object: definition.Captures);
        var select = Assert.Single(
            collection: definition.Cameras,
            predicate: camera => (camera.Name == "district-select")
        );
        var op = Assert.IsType<WorldCameraProgramOp.SelectProgram>(@object: Assert.Single(collection: select.Rig.Operations));

        Assert.Contains(
            collection: definition.Views.Layouts,
            filter: layout => ((layout.Name == "parity") && (layout.Slots[0].Camera == "district-select"))
        );

        foreach (var district in Districts) {
            var station = Assert.Single(
                collection: captures.Rows,
                predicate: row => (row.Station.ToString() == district)
            );

            Assert.Single(collection: station.Ticks);
            Assert.Contains(
                collection: op.Cases,
                filter: entry => (entry.Program == ((district == "plaza")
                ? "plaza-cam"
                : $"{district}-arrival-cam"))
            );
        }

        Assert.Contains(
            collection: captures.Rows,
            filter: row => (row.Station.ToString() == "plaza")
        );
    }
    [Fact]
    public void AFreshSeatWakesInTheStudioBehindASolidGateThatOpensOnceAwakened() {
        var definition = AuthoredGameFixtures.Nexus;

        // The reserved identity lane the island declares so a body's fact travels through $identity:<bodyRef>:<fact>.
        var lane = Assert.Single(
            collection: definition.State,
            predicate: row => (row.Name == "identity")
        );

        Assert.Equal(
            CellKind.Int,
            lane.Kind
        );
        Assert.True(condition: lane.IsKeyed);

        // A returning identity's fact re-poses each local seat at the plaza; a fresh one never satisfies the gate,
        // so it keeps the seatSpawns-authored studio arrival above.
        var seat1Wake = Assert.Single(
            collection: definition.Rules!,
            predicate: rule => (rule.Name.ToString() == "seat1-returning-identity-wakes-at-plaza")
        );
        var seat1Pose = Assert.IsType<WorldEffect.Pose>(@object: Assert.Single(collection: seat1Wake.Effects));
        var seat1Gate = Assert.IsType<ActionPredicate.CompareState>(@object: seat1Wake.Gate);

        Assert.Equal(
            "plaza-1",
            seat1Pose.SpawnPoint
        );
        Assert.Equal(
            "$identity:body:0:awakened",
            seat1Gate.State
        );
        Assert.Equal(
            ActionTriggerMode.Edge,
            seat1Wake.Mode
        );

        var seat2Wake = Assert.Single(
            collection: definition.Rules!,
            predicate: rule => (rule.Name.ToString() == "seat2-returning-identity-wakes-at-plaza")
        );
        var seat2Pose = Assert.IsType<WorldEffect.Pose>(@object: Assert.Single(collection: seat2Wake.Effects));
        var seat2Gate = Assert.IsType<ActionPredicate.CompareState>(@object: seat2Wake.Gate);

        Assert.Equal(
            "plaza-2",
            seat2Pose.SpawnPoint
        );
        Assert.Equal(
            "$identity:body:1:awakened",
            seat2Gate.State
        );

        // The gate stands solid between the studio and the plaza until either seat's fact is set.
        var gate = Assert.Single(
            collection: definition.Placements,
            predicate: row => (row.Id == "studioGate")
        );

        Assert.Equal(
            "studioGateClosed",
            gate.PrototypeId
        );
        Assert.Equal(
            "studioCourt",
            gate.Parent
        );
        Assert.NotNull(@object: gate.Solid);
        Assert.All(
            gate.Respond!,
            response => Assert.Equal(
                "studioGateOpen",
                response.PrototypeId
            )
        );

        // The turntable's look cycle also marks the identity awakened, on the same edge that advances the counter.
        var lookCycle = Assert.Single(
            collection: definition.Rules!,
            predicate: rule => (rule.Name.ToString() == "studio_studio-look-cycle")
        );

        Assert.Contains(
            collection: lookCycle.Effects,
            filter: effect => ((effect is WorldEffect.SetIdentityFact fact) && (fact.Fact == "awakened"))
        );
    }
    [Fact]
    public void EachArchFramesItsDistrictThroughAViewScreen() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var district in Arches) {
            var arch = Assert.Single(
                collection: definition.Placements,
                predicate: row => (row.Id == $"arch-{district}")
            );
            var face = Assert.Single(collection: arch.FaceSources!);
            var view = Assert.IsType<WorldScreenSource.View>(@object: face.Source);

            Assert.Equal(
                $"{district}-arrival-cam",
                view.CameraName
            );
            Assert.NotNull(@object: arch.Solid);
            Assert.Contains(
                collection: definition.Cameras,
                filter: camera => (camera.Name == view.CameraName)
            );
        }
    }
    [Fact]
    public void EveryDistrictHasAnArrivalSpawnAndACourtPlacement() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var district in Districts) {
            Assert.Contains(
                collection: definition.SpawnPoints,
                filter: spawn => (spawn.Id == $"{district}-arrival")
            );
        }

        foreach (var court in new[] { "diveCourt", "kartCourt", "jumpCourt", "studioCourt", "arenaCourt", "arcadeCourt", "granaryCourt", "marketCourt", "provingCourt", "gardenCourt" }) {
            var placement = Assert.Single(
                collection: definition.Placements,
                predicate: row => (row.Id == court)
            );

            Assert.Null(@object: placement.Parent);
            Assert.Null(@object: placement.Distribution);
            Assert.Equal(
                1f,
                placement.Scale
            );
        }

        Assert.Equal(
            ["studio-arrival", "studio-arrival"],
            definition.Population.SeatSpawns
        );
    }
    [Fact]
    public void EveryPlanetoidCarriesARadialGravityArea() {
        var definition = AuthoredGameFixtures.Nexus;
        var planetoids = definition.Placements.Where(predicate: row => (row.PrototypeId == "planetoid")).ToArray();

        Assert.Equal(
            5,
            planetoids.Length
        );

        foreach (var planetoid in planetoids) {
            var area = Assert.Single(
                collection: definition.Gravity.Areas!,
                predicate: area => (area.PlacementId == planetoid.Id)
            );

            Assert.Equal(
                WorldGravityAreaMode.Replace,
                area.Mode
            );
            Assert.IsType<WorldGravityAreaAcceleration.Radial>(@object: area.Acceleration);
            Assert.NotNull(@object: planetoid.Solid);
        }
    }
    [Fact]
    public void TheGamesStandOnTheMarketHallFloor() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var root in new[] { "hexTable", "mancalaTable", "billiardsTray", "bowlingLane", "dominoRun" }) {
            var placement = Assert.Single(
                collection: definition.Placements,
                predicate: row => (row.Id == root)
            );

            Assert.Equal(
                "marketCourt",
                placement.Parent
            );
        }

        Assert.Contains(
            collection: definition.Navigation.Rows,
            filter: domain => (domain.Name == "market")
        );
    }
    [Fact]
    public void TheIslandImportsEveryDistrictUnderItsAlias() {
        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                Path.Combine(
                    path1: AuthoredGameFixtures.Root,
                    path2: "src/Puck.World/Assets/worlds/puck.world.json"
                ),
                out _,
                out var reason
            ),
            userMessage: reason
        );

        var raw = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: "src/Puck.World/Assets/worlds/puck.world.json"
        )))!;
        var imports = raw["imports"]!.AsArray();

        foreach (var alias in new[] { "granaries", "arcade", "dive", "kart", "jump", "arena", "studio" }) {
            var entry = Assert.Single(
                collection: imports,
                predicate: row => (row?["as"]?.GetValue<string>() == alias)
            );

            Assert.Equal(
                $"modules/{alias}.world.json",
                entry!["document"]!.GetValue<string>()
            );
        }
    }
    [Fact]
    public void TheSplitLayoutSeatsTwoBesideTheActionLayout() {
        var layouts = AuthoredGameFixtures.Nexus.Views.Layouts;
        var action = Assert.Single(
            collection: layouts,
            predicate: layout => (layout.Name == "action")
        );
        var split = Assert.Single(
            collection: layouts,
            predicate: layout => (layout.Name == "split")
        );

        Assert.Single(collection: action.Slots);
        Assert.Equal(
            2,
            split.Slots.Count
        );
        Assert.All(
            split.Slots,
            slot => Assert.Null(@object: slot.Camera)
        );
        Assert.Equal(
            1f,
            (split.Slots[0].Width + split.Slots[1].Width)
        );
        Assert.Equal(
            2,
            AuthoredGameFixtures.Nexus.Population.LocalSeats
        );
    }
}
