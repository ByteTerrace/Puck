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

    private static WorldFieldsSection FilledFields(WorldLatticeFill fill, int width = 32, int depth = 32) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: width,
            Depth: depth,
            Layers: 1,
            StepEveryTicks: 1
        ),
        Fields: [new WorldFieldRow(Name: "grass", Min: 0f, Max: 1f)],
        Paint: [(fill with { Field = "grass" })]
    );
    private static long SumBits(WorldFieldLattice lattice, int cells) {
        var sum = 0L;

        for (var cell = 0; (cell < cells); cell++) {
            sum = unchecked(sum + (lattice.Value(field: 0, cell: cell).Value * 31L));
        }

        return sum;
    }

    [Fact]
    public void ANoiseFillIsBitIdenticalAcrossConstructionsAndMovesWithTheWorldSeed() {
        var fill = new WorldLatticeFill.Noise(Value: 1f, Frequency: 8, Threshold: 0.4f, Octaves: 3, Seed: 7u);
        var a = new WorldFieldLattice(document: FilledFields(fill: fill), worldSeed: 5UL);
        var b = new WorldFieldLattice(document: FilledFields(fill: fill), worldSeed: 5UL);
        var rerolled = new WorldFieldLattice(document: FilledFields(fill: fill), worldSeed: 6UL);
        var filled = 0;

        for (var cell = 0; (cell < (32 * 32)); cell++) {
            Assert.Equal(
                actual: b.Value(field: 0, cell: cell).Value,
                expected: a.Value(field: 0, cell: cell).Value
            );

            if (a.Value(field: 0, cell: cell).Value != 0L) {
                filled++;
            }
        }

        // Patchy, not degenerate: some cells filled, some not, and the world seed rerolls the pattern.
        Assert.InRange(actual: filled, high: ((32 * 32) - 1), low: 1);
        Assert.NotEqual(
            actual: SumBits(lattice: rerolled, cells: (32 * 32)),
            expected: SumBits(lattice: a, cells: (32 * 32))
        );
    }

    [Fact]
    public void AScatterFillWritesDiscsAndNothingOutsideThem() {
        var fill = new WorldLatticeFill.Scatter(Value: 1f, Spacing: 8, Radius: 2, Seed: 3u);
        var lattice = new WorldFieldLattice(document: FilledFields(fill: fill), worldSeed: 1UL);
        var filled = 0;

        for (var cell = 0; (cell < (32 * 32)); cell++) {
            if (lattice.Value(field: 0, cell: cell).Value != 0L) {
                filled++;
            }
        }

        // 16 blocks of one disc each: pi*r^2 ~ 13 cells per disc; discs never merge (radius <= spacing/2), so the
        // count stays within the per-block disc envelope on BOTH sides.
        Assert.InRange(actual: filled, high: (16 * 21), low: 16);
    }

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

    // The document spelling of a composite: state.lattices topology + one lattice-shaped row per composite row —
    // what `with { Fields = ... }` said before the fold made Fields a compiled view of the state section.
    private static WorldDefinition WithLattice(WorldDefinition definition, WorldFieldsSection composite) =>
        (definition with { StateRaw = WorldFieldsSection.ToStateSection(composite: composite) });

    [Fact]
    public void PopulationCreatesFields_WhenCollisionAndTargetsDoNotRequireAnSdfField() {
        var definition = WithLattice(definition: Fixtures.BuildDocument(), composite: Fields());

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason),
            userMessage: reason
        );

        var population = new WorldPopulation(definition: definition);

        Assert.NotNull(@object: population.Fields);
    }

    [Fact]
    public void ValidatorRefusesHeightGeometryThatCannotFitOneRenderBrick() {
        var tooWide = WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(width: (WorldFieldCapacity.MaxSurfaceCells + 1), heightScale: 1f));
        var tooTall = WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(layers: 2, heightScale: 64f));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooWide, neighbours: null, reason: out var wideReason));
        Assert.Contains(expectedSubstring: "render brick", actualString: wideReason, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooTall, neighbours: null, reason: out var tallReason));
        Assert.Contains(expectedSubstring: "across 2 layers", actualString: tallReason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRefusesValuesThatCollapseOrChangeMeaningAtTheFixedPointBoundary() {
        var tinyCell = WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(cellSize: 0.000001f));
        var invalidComparison = WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(reactions: [
            new WorldReaction.Transform(
                When: [new WorldFieldCondition(Field: "heat", Comparison: ((WorldFieldComparison)byte.MaxValue), Value: 0f)],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Set, Value: 1f)]
            ),
        ]));

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
