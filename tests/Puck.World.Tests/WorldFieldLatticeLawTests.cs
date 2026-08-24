using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Pins the field lattice's authoritative state, wire-delta, validation, and checkpoint boundaries.</summary>
public sealed class WorldFieldLatticeLawTests {
    private static WorldFieldsSection Fields(
        int width = 1,
        int depth = 1,
        int layers = 1,
        float cellSize = 1f,
        float heightScale = 0f,
        IReadOnlyList<WorldReaction>? reactions = null
    ) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: cellSize,
            Width: width,
            Depth: depth,
            Layers: layers,
            StepEveryTicks: 1
        ),
        Fields: [new WorldFieldRow(Name: "heat", Min: 0f, Max: 10f, HeightScale: heightScale, Color: (heightScale > 0f ? "#ffffff" : null))],
        Reactions: reactions
    );

    private static void Step(WorldFieldLattice lattice) => lattice.Step(
        tick: 1,
        bodyCount: 0,
        bodyPosition: _ => null,
        readTag: (_, _) => 0,
        writeTag: (_, _, _) => { }
    );

    [Fact]
    public void ABodyStandingOnAGroundLatticeSurfaceCouplesToItsColumn() {
        // Layers = 1, cellSize 1, heightScale 2, max 10: the derived coupling ceiling is 1 + 2*10 = 21. A body at
        // y = 1.5 stands ABOVE the one-voxel slab (a bare inside test refuses it) yet ON a plausible surface, so the
        // emit reaction must deposit into its column.
        var lattice = new WorldFieldLattice(document: Fields(
            heightScale: 2f,
            reactions: [new WorldReaction.Emit(Tag: "hot", Field: "heat", Amount: 4f)]
        ));

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            bodyPosition: _ => new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.5),
                Y: FixedQ4816.FromDouble(value: 1.5),
                Z: FixedQ4816.FromDouble(value: 0.5)
            ),
            readTag: (_, _) => 1,
            writeTag: (_, _, _) => { }
        );

        Assert.Equal(
            expected: FixedQ4816.FromInteger(value: 4).Value,
            actual: lattice.Value(field: 0, cell: 0).Value
        );
    }

    [Fact]
    public void ABodyAboveTheDerivedCouplingCeilingDoesNotCouple() {
        // Same lattice: ceiling 21. A body at y = 30 flies far above any reachable surface — no deposit.
        var lattice = new WorldFieldLattice(document: Fields(
            heightScale: 2f,
            reactions: [new WorldReaction.Emit(Tag: "hot", Field: "heat", Amount: 4f)]
        ));

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            bodyPosition: _ => new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.5),
                Y: FixedQ4816.FromInteger(value: 30),
                Z: FixedQ4816.FromDouble(value: 0.5)
            ),
            readTag: (_, _) => 1,
            writeTag: (_, _, _) => { }
        );

        Assert.Equal(
            expected: 0L,
            actual: lattice.Value(field: 0, cell: 0).Value
        );
    }

    [Fact]
    public void MultipleWritesToOneCell_DeliverOneFinalDelta() {
        var lattice = new WorldFieldLattice(document: Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [
                    new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f),
                    new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 2f),
                ]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        Step(lattice: lattice);

        var deltas = lattice.TakeDeltas(full: false, isFull: out var full);

        Assert.False(condition: full);
        var delta = Assert.Single(collection: deltas);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3).Value, actual: delta.Raw);
    }

    [Fact]
    public void PrimerFullTake_DoesNotConsumeSharedIncrementalDeltas() {
        var lattice = new WorldFieldLattice(document: Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        Step(lattice: lattice);

        var primer = lattice.TakeDeltas(full: true, isFull: out var primerFull);
        var incremental = lattice.TakeDeltas(full: false, isFull: out var incrementalFull);

        Assert.True(condition: primerFull);
        Assert.Single(collection: primer);
        Assert.False(condition: incrementalFull);
        Assert.Single(collection: incremental);
    }

    [Fact]
    public void PopulationCreatesFields_WhenCollisionAndTargetsDoNotRequireAnSdfField() {
        var definition = (Fixtures.BuildDocument() with { Fields = Fields() });

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason),
            userMessage: reason
        );

        var population = new WorldPopulation(definition: definition);

        Assert.NotNull(@object: population.Fields);
    }

    [Fact]
    public void ValidatorRefusesHeightGeometryThatCannotFitOneRenderBrick() {
        var tooWide = (Fixtures.BuildDocument() with { Fields = Fields(width: (WorldFieldCapacity.MaxSurfaceCells + 1), heightScale: 1f) });
        var tooTall = (Fixtures.BuildDocument() with { Fields = Fields(layers: 2, heightScale: 64f) });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooWide, neighbours: null, reason: out var wideReason));
        Assert.Contains(expectedSubstring: "render brick", actualString: wideReason, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooTall, neighbours: null, reason: out var tallReason));
        Assert.Contains(expectedSubstring: "across 2 layers", actualString: tallReason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRefusesValuesThatCollapseOrChangeMeaningAtTheFixedPointBoundary() {
        var tinyCell = (Fixtures.BuildDocument() with { Fields = Fields(cellSize: 0.000001f) });
        var invalidComparison = (Fixtures.BuildDocument() with { Fields = Fields(reactions: [
            new WorldReaction.Transform(
                When: [new WorldFieldCondition(Field: "heat", Comparison: ((WorldFieldComparison)byte.MaxValue), Value: 0f)],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Set, Value: 1f)]
            ),
        ]) });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tinyCell, neighbours: null, reason: out var cellReason));
        Assert.Contains(expectedSubstring: "quantize to a positive Q48.16", actualString: cellReason, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: invalidComparison, neighbours: null, reason: out var comparisonReason));
        Assert.Contains(expectedSubstring: "comparison", actualString: comparisonReason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreRefusesCellValuesOutsideTheAuthoredRangeBeforeWritingAnything() {
        var lattice = new WorldFieldLattice(document: Fields());
        var invalid = new WorldFieldLattice.WorldFieldCheckpoint(Raw: [[FixedQ4816.FromInteger(value: 11).Value]]);

        Assert.Throws<InvalidOperationException>(testCode: () => lattice.Restore(checkpoint: invalid));
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(field: 0, cell: 0));
    }
}
