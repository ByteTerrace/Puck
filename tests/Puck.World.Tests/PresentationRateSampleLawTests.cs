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
        static WorldStateRow Row(string name, int value) => new(CellName.Parse(candidate: name), CellKind.Int,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: value))]);
        var definition = Fixtures.BuildDocument().WithWorldState([Row(name: "scale", value: 7), Row(name: "low", value: 1), Row(name: "axis", value: 1)]) with {
            RenderRaw = new WorldRenderDefaults(
                Sky: new(Layers: [new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar(binding: "state.scale")) { Name = "air" }]),
                Lighting: new(Curvature: new(InkLow: new BindableScalar(binding: "state.low"), InkHigh: 2f)),
                Environment: new(Softboxes: [new WorldRenderSoftbox(new(x: new BindableScalar(binding: "state.axis"), y: 0f, z: 0f), new(x: 1f, y: 1f)) { Name = "panel" }])),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        Assert.Equal((3, 6), PresentationSeedCensus.Write(definition: definition, name: "scalar-pair-direction fixture (admitted)"));
    }
    [Fact]
    public void Every_shipped_worlds_rate_storage_is_counted_through_its_document_carrier() {
        var directory = Path.Combine(path1: AuthoredGameFixtures.Root, path2: ShippedWorldDocuments.WorldDirectory);

        Assert.True(condition: PuckDocumentComposer.TryCarriers(carriers: out var carriers, directory: directory, libraries: out var libraries, option: SearchOption.AllDirectories, reason: out var reason), userMessage: reason);
        var referrer = Path.Combine(path1: directory, path2: "rate-census.world.json");
        var worlds = 0;
        var fragments = 0;
        var nonzero = new List<string>();

        foreach (var carrier in carriers) {
            Assert.True(condition: PuckDocumentComposer.Instance.TryRead(carrier.Name, referrer, out var resolved, out var bytes, out reason), userMessage: reason);
            Assert.True(condition: PuckDocumentComposer.TryComposeWorldDocument(rootBytes: bytes!, rootResolvedPath: resolved,
                composed: out var composed, chainBytes: out _, reason: out reason), userMessage: reason);
            var document = (composed ?? JsonNode.Parse(bytes)!.AsObject());

            if (document["schema"]?.GetValue<string>() != WorldDefinition.SchemaVersion) { fragments++; continue; }
            // Count prepared presentation storage before an instance draws gameplay state. No analytic rate may
            // depend on that state; the carrier's full admission is exercised by the authored-world suite.
            var definition = document.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)!;
            var compiled = WorldPresentationRates.Of(definition: definition);

            Assert.Empty(collection: compiled.Errors);
            Assert.InRange(compiled.Cost.Coefficients, 0, WorldPresentationRates.MaximumCoefficients);
            TestContext.Current.TestOutputHelper!.WriteLine(message: $"{carrier.Name}: pieces={compiled.Cost.Pieces}; coefficients={compiled.Cost.Coefficients}; coefficient-bytes={compiled.Cost.CoefficientBytes}; rate-degree={compiled.Cost.Degree}");
            PresentationSeedCensus.Write(carrier.Name, definition);
            if (compiled.Cost.Coefficients != 0) { nonzero.Add(item: carrier.Name); }
            worlds++;
        }
        Assert.Equal("sky-clock-keys", Assert.Single(collection: nonzero));
        TestContext.Current.TestOutputHelper!.WriteLine(message: $"counted-worlds={worlds}; positive-rate-storage-worlds={nonzero.Count}; fragments={fragments}; libraries={libraries.Count}");
    }
    [InlineData("sky-clock-keys")]
    [InlineData("moth-courtyard")]
    [InlineData("puck")]
    [Theory]
    public void Shipped_world_rates_report_their_retained_coefficient_storage(string name) {
        var path = Path.Combine(AuthoredGameFixtures.Root, "src", "Puck.World", "Assets", "worlds", (name + ((name == "puck") ? ".world.json" : ".puck")));
        WorldDefinition? definition;

        if (name == "puck") { definition = AuthoredGameFixtures.Nexus; } else { Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path, out definition, out var reason), userMessage: reason); }
        var compiled = WorldPresentationRates.Of(definition: definition!);

        PresentationSeedCensus.Write(definition: definition!, name: (name + " (admitted)"));
        Assert.Empty(collection: compiled.Errors);
        var cost = compiled.Cost;

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"{name}: pieces={cost.Pieces}; coefficients={cost.Coefficients}; coefficient-bytes={cost.CoefficientBytes}; rate-degree={cost.Degree}");
        if (name == "sky-clock-keys") {
            Assert.Equal(new WorldRateCost(Coefficients: 104, Degree: 9, Pieces: 12), cost);
        } else {
            Assert.Equal(0, cost.Coefficients);
        }
    }
}
