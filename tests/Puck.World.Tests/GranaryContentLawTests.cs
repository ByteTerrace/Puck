using Xunit;

using System.Text.Json.Nodes;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Locks the authored granary district to its growth contract: dealt stores expose the shared spatial
/// occupation/clearance vocabulary, the water and power markers publish independent influence channels, and the
/// keyed coverage rows plus exported requests give a host a bounded survey and expansion control surface.</summary>
public sealed class GranaryContentLawTests {
    private const string Alias = "granaries_";

    private static string ModulePath() => Path.Combine(
        AuthoredGameFixtures.Root,
        "src",
        "Puck.World",
        "Assets",
        "worlds",
        "modules",
        "granaries.world.json"
    );

    private static WorldDefinition GranaryModule() {
        // Keep the runtime fixture small while still crossing the real file composition boundary. The module is
        // imported under its shipped alias, so the test observes the same namespaced rows/rules/placements the
        // host world receives.
        var host = JsonNode.Parse(Fixtures.DefaultWorldBytes())!.AsObject();
        // These sections are intentionally absent on the host so the imported fragment supplies its complete
        // keyed rows. An empty host section would otherwise win the fragment fold before alias validation.
        host.Remove("state");
        host.Remove("placements");
        host.Remove("prototypes");
        host.Remove("navigation");
        host["imports"] = new JsonArray(new JsonObject {
            ["as"] = "granaries",
            ["document"] = ModulePath(),
        });

        var path = Path.Combine(Path.GetTempPath(), $"puck-granary-{Guid.NewGuid():N}.world.json");
        File.WriteAllText(path, host.ToJsonString());
        try {
            Assert.True(
                condition: WorldDefinitionLoader.TryLoadFile(path: path, definition: out var definition, reason: out var reason),
                userMessage: reason
            );
            return definition!;
        } finally {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void GranaryModuleDeclaresCoverageProvidersAndControls() {
        // Modules are fragments. Compose the isolated host through the same alias import path used by the running
        // world instead of accidentally treating a fragment as a standalone definition.
        var definition = GranaryModule();

        var stores = Assert.Single(collection: definition.Placements, predicate: static placement => placement.Id == "granaryStores");
        Assert.Contains(collection: stores.Spatial!, filter: static volume => volume.Role == WorldPlacementSpatialRole.Occupation);
        Assert.Contains(collection: stores.Spatial!, filter: static volume => volume.Role == WorldPlacementSpatialRole.Clearance);

        var irrigation = Assert.Single(collection: definition.Placements, predicate: static placement => placement.Id == "granaryIrrigation");
        var power = Assert.Single(collection: definition.Placements, predicate: static placement => placement.Id == "granaryPower");
        Assert.Equal(expected: "water", actual: Assert.Single(irrigation.Spatial!).Channel);
        Assert.Equal(expected: "power", actual: Assert.Single(power.Spatial!).Channel);
        Assert.Equal(expected: "granaryIrrigation", actual: irrigation.PrototypeId);
        Assert.Equal(expected: "granaryPower", actual: power.PrototypeId);

        var table = Assert.Single(collection: definition.Placements, predicate: static placement => placement.Id == "granarySurveyTable");
        Assert.Equal(expected: "granarySurveyTable", actual: table.PrototypeId);
        var tablePrototype = Assert.Single(collection: definition.Creations, predicate: static creation => creation.Id == "granarySurveyTable");
        Assert.Contains(collection: tablePrototype.Document.TextRuns!, filter: static run => run.Text == "SURVEY TABLE");

        Assert.Contains(collection: definition.Rules!, filter: rule => rule.Name.ToString() == $"{Alias}granary-water-coverage");
        Assert.Contains(collection: definition.Rules!, filter: rule => rule.Name.ToString() == $"{Alias}granary-power-coverage");
        Assert.Contains(collection: definition.Rules!, filter: rule => rule.Name.ToString() == $"{Alias}granary-survey-cycle");
    }

    [Fact]
    public void GranaryModuleLoadsThroughTheShippedNexus() {
        var definition = AuthoredGameFixtures.Nexus;

        Assert.Contains(collection: definition.Placements, filter: static placement => placement.Id == "granaryStores");
        Assert.Contains(collection: definition.Placements, filter: static placement => placement.Id == "granaryIrrigation");
        Assert.Contains(collection: definition.Placements, filter: static placement => placement.Id == "granaryPower");
        Assert.Contains(collection: definition.Rules!, filter: rule => rule.Name.ToString() == $"{Alias}granary-irrigation-stage");
    }

    [Fact]
    public void CoverageRowsAreKeyedAndBoundedToTheDealCapacity() {
        var definition = GranaryModule();

        foreach (var name in new[] { "granaryNames", "granaryWaterCoverage", "granaryPowerCoverage" }) {
            var row = Assert.Single(collection: definition!.State, predicate: state => state.Name.Value == $"{Alias}{name}");
            Assert.Equal(expected: 16, actual: row.Capacity);
            Assert.Empty(collection: row.Cells ?? []);
        }

        Assert.Equal(expected: 7, actual: definition!.Rules!.Count(rule => rule.Name.ToString().StartsWith(Alias + "granary-", StringComparison.Ordinal)));
    }

    [Fact]
    public void IrrigationRequestGrowsTheProviderThroughAnAtomicWorldRule() {
        var definition = GranaryModule();
        using var fixture = Fixtures.FreshServer(definition: definition);

        var discovered = Enumerable.Range(start: 1, count: 16)
            .Select(index => (WorldMutation)new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: $"{Alias}granaryNames",
                Key: $"bytrcstp{index:000}",
                Value: 0,
                Kind: WorldDocumentWriteKind.Set,
                Text: $"bytrcstp{index:000}"
            ))
            .ToArray();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Batch(Principal: WorldPrincipal.Console, Mutations: discovered));
        fixture.Step();

        var providerFrame = fixture.Server.Definition.PlacementFrames["granaryIrrigation"];
        var uncovered = fixture.Server.Definition.Placements
            .Where(placement => placement.Id.StartsWith("granaryStores/", StringComparison.Ordinal))
            .FirstOrDefault(placement => {
                if (fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", placement.Id) != 0 ||
                    !fixture.Server.Definition.PlacementFrames.TryGetValue(placement.Id, out var targetFrame)) {
                    return false;
                }

                var delta = targetFrame.Position - providerFrame.Position;
                return MathF.Abs(delta.X) <= 6.5f && MathF.Abs(delta.Z) <= 6.5f;
            });
        Assert.NotNull(@object: uncovered);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: $"{Alias}granaryIrrigationRequest",
            Key: WorldStateRow.SlotKey.Value,
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var stage = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, $"{Alias}granaryIrrigationStage")!;
        Assert.Equal(expected: 1L, actual: stage.Cells!.Single().Value);

        var provider = Assert.Single(collection: fixture.Server.Definition.Placements, predicate: static placement => placement.Id == "granaryIrrigation");
        Assert.Equal(expected: 1f, actual: provider.Scale);
        var coverage = Assert.Single(collection: provider.Spatial!, predicate: static volume => volume.Name == "irrigation-coverage");
        Assert.Equal(expected: 6.5f, actual: coverage.Shape.HalfExtents.X);
        Assert.True(condition: fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", uncovered!.Id) > 0);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: $"{Alias}granaryIrrigationRequest",
            Key: WorldStateRow.SlotKey.Value,
            Value: 2,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        stage = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, $"{Alias}granaryIrrigationStage")!;
        Assert.Equal(expected: 2L, actual: stage.Cells!.Single().Value);
        provider = Assert.Single(collection: fixture.Server.Definition.Placements, predicate: static placement => placement.Id == "granaryIrrigation");
        coverage = Assert.Single(collection: provider.Spatial!, predicate: static volume => volume.Name == "irrigation-coverage");
        Assert.Equal(expected: 8f, actual: coverage.Shape.HalfExtents.X);
        Assert.True(condition: fixture.Server.Definition.SpatialQueryIndex.CountInfluences("water", uncovered.Id) > 0);

        // Coverage rules run before expansion rules and observe the new stage on the following tick.
        fixture.Step();
        var quiescentDefinition = fixture.Server.Definition;
        fixture.Step();
        Assert.Same(expected: quiescentDefinition, actual: fixture.Server.Definition);

        var removedKey = uncovered.Id["granaryStores/".Length..];
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(
            Principal: WorldPrincipal.Console,
            Row: $"{Alias}granaryNames",
            Key: removedKey
        ));
        fixture.Step();

        Assert.DoesNotContain(
            collection: fixture.Server.Definition.Placements,
            filter: placement => placement.Id == uncovered.Id
        );

        // Deal removal runs after world rules. The cleanup rules observe the now-absent influence on the following
        // quiescent tick, while the source removal itself remains atomic and the deal child is already gone.
        fixture.Step();
        foreach (var coverageName in new[] { "granaryWaterCoverage", "granaryPowerCoverage" }) {
            var coverageRow = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, $"{Alias}{coverageName}")!;
            Assert.DoesNotContain(collection: coverageRow.Cells ?? [], filter: cell => cell.Key.Value == removedKey);
        }
    }
}
