using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Malformed nullable document rows refuse through the rule compiler's named boundary, and pose fields the
/// selected addressing mode cannot consume are rejected instead of being silently discarded.</summary>
public sealed class WorldRuleCompilerAdversarialLawTests {
    [Fact]
    public void BindableCameraScalars_ExportTheirNumberOrStringWireShape() {
        var split = WorldSchema.Export(postRenderExtensions: []);
        var definitions = Assert.IsType<JsonObject>(@object: split.Common["$defs"]);
        var orbit = Assert.IsType<JsonObject>(@object: definitions["WorldCameraProgramOpOrbit"]);
        var properties = Assert.IsType<JsonObject>(@object: orbit["properties"]);

        foreach (var property in new[] { "yaw", "pitch" }) {
            var node = Assert.IsType<JsonObject>(@object: properties[property]);
            var types = Assert.IsType<JsonArray>(@object: node["type"]);

            Assert.Equal(
                expected: ["number", "string"],
                actual: types.Select(selector: static type => type!.GetValue<string>())
            );
        }
    }

    [Fact]
    public void NullEffectsList_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(effects: null!),
        expected: "non-empty effect list"
    );

    [Fact]
    public void NullEffectRow_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(effects: [null!]),
        expected: "effect row is null"
    );

    [Fact]
    public void NullAllPredicateList_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(
            effects: [new ActionEffect.Save()],
            gate: new ActionPredicate.All(Predicates: null!)
        ),
        expected: "non-null predicate list"
    );

    [Fact]
    public void NullPredicateInsideAll_RefusesByNameRatherThanBeingIgnored() => Refuses(
        rule: Rule(
            effects: [new ActionEffect.Save()],
            gate: new ActionPredicate.All(Predicates: [null!])
        ),
        expected: "null predicate row"
    );

    [Fact]
    public void NullInteractionEffectsList_RefusesByNameRatherThanThrowingNullReference() {
        const string Property = "probe";
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: WorldCellName.Parse(candidate: Property),
                Kind: CellKind.Int,
                Capacity: 1,
                Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 1L)]
            )]),
            Properties = new WorldPropertyRegistrySection(Names: [Property]),
            Interactions = new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: WorldCellName.Parse(candidate: "adversarialInteraction"),
                Left: Property,
                Right: Property,
                CoOccurrence: WorldInteractionCoOccurrence.Distance,
                Range: 1f,
                Effects: null!
            )]),
        };

        var exception = Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.CompileAllInteractions(definition: definition));

        Assert.Contains(
            expectedSubstring: "non-empty effect list",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }

    [Fact]
    public void SpawnPointPose_WithLiteralAngles_RefusesRatherThanDiscardingAngles() => Refuses(
        rule: Rule(effects: [new ActionEffect.Pose(
            Key: "0",
            SpawnPoint: WorldSpawnPointDefaults.ImplicitOriginId,
            YawDegrees: 15f
        )]),
        expected: "angles are only legal with a literal 'position'"
    );

    private static WorldRule Rule(IReadOnlyList<ActionEffect> effects, ActionPredicate? gate = null) => new(
        Name: WorldCellName.Parse(candidate: "adversarial"),
        Effects: effects,
        Gate: gate
    );

    private static void Refuses(WorldRule rule, string expected) {
        var exception = Assert.Throws<WorldRuleException>(() => WorldRuleCompiler.Compile(
            rule: rule,
            definition: Fixtures.BuildDocument()
        ));

        Assert.Contains(
            expectedSubstring: expected,
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
}
