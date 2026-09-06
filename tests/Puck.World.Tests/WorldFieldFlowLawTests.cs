using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>Pins the authoring vocabulary's own refusals over a <see cref="WorldReaction.Flow"/> row's
/// <c>over</c>/<c>spillRow</c> fields — the kernel's own mass-conservation, equilibrium, and determinism laws live
/// in <c>tests/Puck.Physics.Tests/FieldLatticeFlowLawTests.cs</c>, which needs no server or document.</summary>
public sealed class WorldFieldFlowLawTests {
    private static WorldFieldsSection FlowFields(
        int width,
        int depth,
        float rate = 1f,
        string? spillRow = null
    ) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: width,
            Depth: depth,
            Layers: 1,
            StepEveryTicks: 1
        ),
        Fields: [
            new WorldFieldRow(Name: "water", Min: 0f, Max: 100f, HeightScale: 1f, Color: "#3B7BD6"),
            new WorldFieldRow(Name: "ground", Min: 0f, Max: 100f, HeightScale: 1f, Color: "#808080"),
        ],
        Reactions: [new WorldReaction.Flow(Field: "water", Over: ["ground"], Rate: rate, SpillRow: spillRow)]
    );

    [Fact]
    public void ValidatorRefusesAnUndeclaredOverFieldASelfReferencingOverEntryAndADuplicateOverEntry() {
        var undeclared = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: FlowFields(width: 2, depth: 2) with {
            Fields = [new WorldFieldRow(Name: "water", Min: 0f, Max: 1f)],
            Reactions = [new WorldReaction.Flow(Field: "water", Rate: 1f, Over: ["missing"])],
        });
        var selfReferencing = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: FlowFields(width: 2, depth: 2) with {
            Reactions = [new WorldReaction.Flow(Field: "water", Rate: 1f, Over: ["water"])],
        });
        var duplicated = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: FlowFields(width: 2, depth: 2) with {
            Reactions = [new WorldReaction.Flow(Field: "water", Rate: 1f, Over: ["ground", "ground"])],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: undeclared, neighbours: null, reason: out var undeclaredReason));
        Assert.Contains(actualString: undeclaredReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "does not declare");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: selfReferencing, neighbours: null, reason: out var selfReason));
        Assert.Contains(actualString: selfReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "itself transports");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: duplicated, neighbours: null, reason: out var duplicateReason));
        Assert.Contains(actualString: duplicateReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "duplicated within over");
    }
    [Fact]
    public void ValidatorRefusesASpillRowThatIsNotADeclaredScalarFixedRow() {
        var missing = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: FlowFields(width: 2, depth: 2, spillRow: "missing"));
        var wrongShapeBase = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: FlowFields(width: 2, depth: 2, spillRow: "keyed"));
        var wrongShape = wrongShapeBase with {
            StateRaw = wrongShapeBase.StateRaw! with {
                World = [.. (wrongShapeBase.StateRaw!.World ?? []), new WorldStateRow(Name: CellName.Parse(candidate: "keyed"), Kind: CellKind.Fixed, Capacity: 4)],
            },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: missing, neighbours: null, reason: out var missingReason));
        Assert.Contains(actualString: missingReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "does not declare");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: wrongShape, neighbours: null, reason: out var shapeReason));
        Assert.Contains(actualString: shapeReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "scalar kind=fixed row");
    }
}
