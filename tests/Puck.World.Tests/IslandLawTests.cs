using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the shape of the one shipped world: every district reachable by its arrival spawn and rooted under
/// its court, the plaza's four arches framing their districts through view screens, the split seat layout beside the
/// action layout, a capture station per district, and a radial gravity area under each planetoid.</summary>
public sealed class IslandLawTests {
    private static readonly string[] Districts = ["dive", "kart", "jump", "studio", "arena", "arcade", "granaries", "market", "proving", "garden"];
    private static readonly string[] Arches = ["dive", "kart", "jump", "studio"];

    [Fact]
    public void EveryDistrictHasAnArrivalSpawnAndACourtPlacement() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var district in Districts) {
            Assert.Contains(definition.SpawnPoints, spawn => spawn.Id == $"{district}-arrival");
        }

        foreach (var court in new[] { "diveCourt", "kartCourt", "jumpCourt", "studioCourt", "arenaCourt", "arcadeCourt", "granaryCourt", "marketCourt", "provingCourt", "gardenCourt" }) {
            var placement = Assert.Single(definition.Placements, row => row.Id == court);

            Assert.Null(placement.Parent);
            Assert.Null(placement.Distribution);
            Assert.Equal(1f, placement.Scale);
        }

        Assert.Equal(["plaza-1", "plaza-2"], definition.Population.SeatSpawns);
    }

    [Fact]
    public void TheIslandImportsEveryDistrictUnderItsAlias() {
        Assert.True(WorldDefinitionFileSource.TryComposeDocumentTree(Path.Combine(AuthoredGameFixtures.Root, "src/Puck.World/Assets/worlds/puck.world.json"), out _, out var reason), reason);

        var raw = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AuthoredGameFixtures.Root, "src/Puck.World/Assets/worlds/puck.world.json")))!;
        var imports = raw["imports"]!.AsArray();

        foreach (var alias in new[] { "granaries", "arcade", "dive", "kart", "jump", "arena", "studio" }) {
            var entry = Assert.Single(imports, row => row?["as"]?.GetValue<string>() == alias);

            Assert.Equal($"modules/{alias}.world.json", entry!["document"]!.GetValue<string>());
        }
    }

    [Fact]
    public void TheGamesStandOnTheMarketHallFloor() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var root in new[] { "tabletop", "hexTable", "mancalaTable", "billiardsTray", "bowlingLane", "dominoRun" }) {
            var placement = Assert.Single(definition.Placements, row => row.Id == root);

            Assert.Equal("marketCourt", placement.Parent);
        }

        Assert.Contains(definition.Navigation.Rows, domain => domain.Name == "market");
    }

    [Fact]
    public void EachArchFramesItsDistrictThroughAViewScreen() {
        var definition = AuthoredGameFixtures.Nexus;

        foreach (var district in Arches) {
            var arch = Assert.Single(definition.Placements, row => row.Id == $"arch-{district}");
            var face = Assert.Single(arch.FaceSources!);
            var view = Assert.IsType<WorldScreenSource.View>(face.Source);

            Assert.Equal($"{district}-arrival-cam", view.CameraName);
            Assert.NotNull(arch.Solid);
            Assert.Contains(definition.Cameras, camera => camera.Name == view.CameraName);
        }
    }

    [Fact]
    public void TheSplitLayoutSeatsTwoBesideTheActionLayout() {
        var layouts = AuthoredGameFixtures.Nexus.Views.Layouts;
        var action = Assert.Single(layouts, layout => layout.Name == "action");
        var split = Assert.Single(layouts, layout => layout.Name == "split");

        Assert.Single(action.Slots);
        Assert.Equal(2, split.Slots.Count);
        Assert.All(split.Slots, slot => Assert.Null(slot.Camera));
        Assert.Equal(1f, split.Slots[0].Width + split.Slots[1].Width);
        Assert.Equal(2, AuthoredGameFixtures.Nexus.Population.LocalSeats);
    }

    [Fact]
    public void ACaptureStationPerDistrictSelectsItsCamera() {
        var definition = AuthoredGameFixtures.Nexus;
        var captures = Assert.IsType<WorldCapturesSection>(definition.Captures);
        var select = Assert.Single(definition.Cameras, camera => camera.Name == "district-select");
        var op = Assert.IsType<WorldCameraProgramOp.Select>(Assert.Single(select.Rig.Operations));

        Assert.Contains(definition.Views.Layouts, layout => (layout.Name == "parity") && (layout.Slots[0].Camera == "district-select"));

        foreach (var district in Districts) {
            var station = Assert.Single(captures.Rows, row => row.Station.ToString() == district);

            Assert.Single(station.Ticks);
            Assert.Contains(op.Cases, entry => entry.Program == ((district == "plaza") ? "plaza-cam" : $"{district}-arrival-cam"));
        }

        Assert.Contains(captures.Rows, row => row.Station.ToString() == "plaza");
    }

    [Fact]
    public void EveryPlanetoidCarriesARadialGravityArea() {
        var definition = AuthoredGameFixtures.Nexus;
        var planetoids = definition.Placements.Where(row => row.PrototypeId == "planetoid").ToArray();

        Assert.Equal(5, planetoids.Length);

        foreach (var planetoid in planetoids) {
            var area = Assert.Single(definition.Gravity.Areas!, area => area.PlacementId == planetoid.Id);

            Assert.Equal(WorldGravityAreaMode.Replace, area.Mode);
            Assert.IsType<WorldGravityAreaAcceleration.Radial>(area.Acceleration);
            Assert.NotNull(planetoid.Solid);
        }
    }
}
