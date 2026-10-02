using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Malformed nullable document rows refuse through the rule compiler's named boundary, and pose fields the
/// selected addressing mode cannot consume are rejected instead of being silently discarded.</summary>
public sealed class WorldFactsCompilerAdversarialLawTests {
    private static void Refuses(WorldRule rule, string expected) {
        var exception = Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.Compile(
            context: WorldFactsCompiler.Context(definition: Fixtures.BuildDocument()),
            rule: rule
        ));

        Assert.Contains(
            expectedSubstring: expected,
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    private static WorldRule Rule(IReadOnlyList<ActionEffect> effects, ActionPredicate? gate = null) => new(
        Name: CellName.Parse(candidate: "adversarial"),
        Effects: effects,
        Gate: gate
    );

    [Fact]
    public void BindableCameraScalars_ExportTheirNumberStringOrClockKeysWireShape() {
        var split = WorldSchema.Export(postProcessPackages: []);
        var definitions = Assert.IsType<JsonObject>(@object: split.Common["$defs"]);
        var orbit = Assert.IsType<JsonObject>(@object: definitions["WorldCameraProgramOpOrbit"]);
        var properties = Assert.IsType<JsonObject>(@object: orbit["properties"]);

        foreach (var property in new[] { "yaw", "pitch" }) {
            var node = Assert.IsType<JsonObject>(@object: properties[property]);
            var constraint = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: node["allOf"])));
            var reference = constraint["$ref"]!.GetValue<string>();

            Assert.Equal(actual: reference, expected: "#/$defs/BindableScalarNonNullable");
            var scalar = Assert.IsType<JsonObject>(@object: definitions["BindableScalarNonNullable"]);
            var alternatives = Assert.IsType<JsonArray>(@object: scalar["anyOf"]);

            Assert.Equal(expected: 3, actual: alternatives.Count);

            Assert.Equal(
                expected: ["number", "string"],
                actual: alternatives.Take(count: 2).Select(selector: static arm => arm!["type"]!.GetValue<string>())
            );
            Assert.Equal(expected: "#/$defs/WorldKeysBindableScalar", actual: alternatives[2]!["$ref"]!.GetValue<string>());
            var keys = Assert.IsType<JsonObject>(@object: definitions["WorldKeysBindableScalar"]);

            Assert.Equal(expected: "object", actual: keys["type"]!.GetValue<string>());
            Assert.False(condition: keys["additionalProperties"]!.GetValue<bool>());
            var keyProperties = Assert.IsType<JsonObject>(@object: keys["properties"]);

            Assert.True(condition: keyProperties.ContainsKey(propertyName: "clock"));
            Assert.True(condition: keyProperties.ContainsKey(propertyName: "keys"));
        }
    }
    [Fact]
    public void EmptyAnyPredicate_RefusesRatherThanCompilingAnAlwaysFalseDeadRule() => Refuses(
        rule: Rule(
            effects: [new WorldEffect.Save()],
            gate: new ActionPredicate.Any(Predicates: [])
        ),
        expected: "at least one predicate"
    );
    [Fact]
    public void EmptyBodyAnyPredicate_RefusesBeforeItCanUnderflowTheRuntimeStack() {
        var gate = new List<CompiledPredicate>();

        var error = Assert.Throws<InvalidOperationException>(testCode: () => BodyActionSpecFactory.FlattenPredicate(
            predicate: new ActionPredicate.Any(Predicates: []),
            gate: gate,
            recencyFacts: [],
            recencyWindows: []
        ));

        Assert.Contains(
            expectedSubstring: "at least one predicate",
            actualString: error.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void NonUnitWorldImpulse_RefusesBecauseRuntimeDoesNotNormalizeIt() => Refuses(
        rule: Rule(effects: [new WorldEffect.PlanarImpulse(
                Key: "0",
                BodyDirection: new DocumentVector3(
                    x: 2f,
                    y: 0f,
                    z: 0f
                ),
                Speed: 1m,
                DurationSeconds: 0.01m
            )]),
        expected: "unit length"
    );
    [Fact]
    public void NullAllPredicateList_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(
            effects: [new WorldEffect.Save()],
            gate: new ActionPredicate.All(Predicates: null!)
        ),
        expected: "non-null predicate list"
    );
    [Fact]
    public void NullEffectRow_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(effects: [null!]),
        expected: "effect row is null"
    );
    [Fact]
    public void NullEffectsList_RefusesByNameRatherThanThrowingNullReference() => Refuses(
        rule: Rule(effects: null!),
        expected: "non-empty effect list"
    );
    [Fact]
    public void NullInteractionEffectsList_RefusesByNameRatherThanThrowingNullReference() {
        const string Property = "probe";
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: Property),
                Kind: CellKind.Int,
                Capacity: 1,
                Cells: [new StateCell(
                        Key: CellName.Parse(candidate: "0"),
                        Value: CellValue.Int(value: 1L)
                    )]
            )]),
            Properties = new WorldPropertyRegistrySection(Names: [Property]),
            Interactions = new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: CellName.Parse(candidate: "adversarialInteraction"),
                Left: Property,
                Right: Property,
                CoOccurrence: WorldInteractionCoOccurrence.Distance,
                Range: 1m,
                Effects: null!
            )]),
        };

        var exception = Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAllInteractions(definition: definition));

        Assert.Contains(
            expectedSubstring: "non-empty effect list",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void NullPredicateInsideAll_RefusesByNameRatherThanBeingIgnored() => Refuses(
        rule: Rule(
            effects: [new WorldEffect.Save()],
            gate: new ActionPredicate.All(Predicates: [null!])
        ),
        expected: "null predicate row"
    );
    [Fact]
    public void OutOfRangeWorldBodyScalar_RefusesByNameRatherThanThrowingOverflow() => Refuses(
        rule: Rule(effects: [new WorldEffect.SetVerticalVelocity(
                Key: "0",
                Velocity: decimal.MaxValue
            )]),
        expected: "outside the Q48.16 range"
    );
    [Fact]
    public void SpawnPointPose_WithLiteralAngles_RefusesRatherThanDiscardingAngles() => Refuses(
        rule: Rule(effects: [new WorldEffect.Pose(
                Key: "0",
                SpawnPoint: WorldSpawnPointDefaults.ImplicitOriginId,
                YawDegrees: 15f
            )]),
        expected: "angles are only legal with a literal 'position'"
    );
    [Fact]
    public void TransactionRefusesNullStateStepsAtCompileTime() => Refuses(
        rule: Rule(effects: [new ActionEffect.Transaction(Effects: [null!])]),
        expected: "an effect row is null"
    );
}
