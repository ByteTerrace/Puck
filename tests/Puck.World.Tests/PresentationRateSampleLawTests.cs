using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Shipped authored worlds supply the counted evidence for the world's compiled-rate storage budget.</summary>
public sealed class PresentationRateSampleLawTests {
    [Fact]
    public void Seed_payload_census_counts_scalar_pair_and_direction_components() {
        static WorldStateRow Row(string name, int value) => new(CellName.Parse(name), CellKind.Int,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value))]);
        var definition = Fixtures.BuildDocument().WithWorldState([Row("scale", 7), Row("low", 1), Row("axis", 1)]) with {
            RenderRaw = new WorldRenderDefaults(
                Sky: new([new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar("state.scale")) { Name = "air" }]),
                Lighting: new(Curvature: new(InkLow: new BindableScalar("state.low"), InkHigh: 2f)),
                Environment: new(Softboxes: [new WorldRenderSoftbox(new(new BindableScalar("state.axis"), 0f, 0f), new(1f, 1f)) { Name = "panel" }]))
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        Assert.Equal((3, 6), PresentationSeedCensus.Write("scalar-pair-direction fixture (admitted)", definition));
    }

    [Fact]
    public void Every_shipped_worlds_rate_storage_is_counted_through_its_document_carrier() {
        var directory = Path.Combine(AuthoredGameFixtures.Root, ShippedWorldDocuments.WorldDirectory);
        Assert.True(PuckDocumentComposer.TryCarriers(directory, SearchOption.AllDirectories, out var carriers, out var libraries, out var reason), reason);
        var referrer = Path.Combine(directory, "rate-census.world.json");
        var worlds = 0;
        var fragments = 0;
        var nonzero = new List<string>();
        foreach (var carrier in carriers) {
            Assert.True(PuckDocumentComposer.Instance.TryRead(carrier.Name, referrer, out var resolved, out var bytes, out reason), reason);
            Assert.True(PuckDocumentComposer.TryComposeWorldDocument(rootBytes: bytes!, rootResolvedPath: resolved,
                composed: out var composed, chainBytes: out _, reason: out reason), reason);
            var document = composed ?? JsonNode.Parse(bytes)!.AsObject();
            if (document["schema"]?.GetValue<string>() != WorldDefinition.SchemaVersion) { fragments++; continue; }
            // Count prepared presentation storage before an instance draws gameplay state. No analytic rate may
            // depend on that state; the carrier's full admission is exercised by the authored-world suite.
            var definition = document.Deserialize(WorldJsonContext.Default.WorldDefinition)!;
            var compiled = WorldPresentationRates.Of(definition);
            Assert.Empty(compiled.Errors);
            Assert.InRange(compiled.Cost.Coefficients, 0, WorldPresentationRates.MaximumCoefficients);
            TestContext.Current.TestOutputHelper!.WriteLine($"{carrier.Name}: pieces={compiled.Cost.Pieces}; coefficients={compiled.Cost.Coefficients}; coefficient-bytes={compiled.Cost.CoefficientBytes}; rate-degree={compiled.Cost.Degree}");
            PresentationSeedCensus.Write(carrier.Name, definition);
            if (compiled.Cost.Coefficients != 0) { nonzero.Add(carrier.Name); }
            worlds++;
        }
        Assert.Equal("sky-clock-keys", Assert.Single(nonzero));
        TestContext.Current.TestOutputHelper!.WriteLine($"counted-worlds={worlds}; positive-rate-storage-worlds={nonzero.Count}; fragments={fragments}; libraries={libraries.Count}");
    }

    [Theory]
    [InlineData("sky-clock-keys")]
    [InlineData("moth-courtyard")]
    [InlineData("puck")]
    public void Shipped_world_rates_report_their_retained_coefficient_storage(string name) {
        var path = Path.Combine(AuthoredGameFixtures.Root, "src", "Puck.World", "Assets", "worlds", name + (name == "puck" ? ".world.json" : ".puck"));
        WorldDefinition? definition;
        if (name == "puck") { definition = AuthoredGameFixtures.Nexus; }
        else { Assert.True(WorldDefinitionLoader.TryLoadFile(path, out definition, out var reason), reason); }
        var compiled = WorldPresentationRates.Of(definition!);
        PresentationSeedCensus.Write(name + " (admitted)", definition!);
        Assert.Empty(compiled.Errors);
        var cost = compiled.Cost;
        TestContext.Current.TestOutputHelper!.WriteLine($"{name}: pieces={cost.Pieces}; coefficients={cost.Coefficients}; coefficient-bytes={cost.CoefficientBytes}; rate-degree={cost.Degree}");
        if (name == "sky-clock-keys") {
            Assert.Equal(new WorldRateCost(Pieces: 12, Coefficients: 104, Degree: 9), cost);
        } else {
            Assert.Equal(0, cost.Coefficients);
        }
    }
}
