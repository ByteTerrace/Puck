using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Pins the mass-conserving <see cref="WorldReaction.Flow"/> transport reaction: exact conservation
/// (including boundary spill), equilibrium under flat terrain, directional movement under a ramp, determinism, and
/// the validator's refusals over its <c>over</c>/<c>spillRow</c> vocabulary.</summary>
public sealed class WorldFieldFlowLawTests {
    // One field ("water", transported) and, when includeGround, a second static terrain field ("ground") the
    // reaction's `over` names. Both fields carry a [0, 100] envelope -- generous enough that the small whole-number
    // test fixtures below never bind a clamp (exact conservation depends on that), and within the maximumRaise
    // ceiling a heightScale-1 field must satisfy.
    private static WorldFieldsSection FlowFields(
        int width,
        int depth,
        int layers = 1,
        float waterHeightScale = 1f,
        bool includeGround = true,
        float groundHeightScale = 1f,
        float rate = 1f,
        string? spillRow = null
    ) {
        var fields = new List<WorldFieldRow> {
            new(Name: "water", Min: 0f, Max: 100f, HeightScale: waterHeightScale, Color: ((waterHeightScale > 0f) ? "#3B7BD6" : null)),
        };
        IReadOnlyList<string>? over = null;

        if (includeGround) {
            fields.Add(item: new WorldFieldRow(Name: "ground", Min: 0f, Max: 100f, HeightScale: groundHeightScale, Color: ((groundHeightScale > 0f) ? "#808080" : null)));
            over = ["ground"];
        }

        return new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                CellSize: 1f,
                Width: width,
                Depth: depth,
                Layers: layers,
                StepEveryTicks: 1
            ),
            Fields: fields,
            Reactions: [new WorldReaction.Flow(Field: "water", Over: over, Rate: rate, SpillRow: spillRow)]
        );
    }
    private static WorldFieldLattice.WorldFieldCheckpoint IntCheckpoint(params int[][] fieldsRaw) => new(
        Raw: [.. fieldsRaw.Select(selector: static values => values.Select(selector: static v => FixedQ4816.FromInteger(value: v).Value).ToArray())]
    );
    private static long SumRaw(WorldFieldLattice lattice, int field, int cellCount) {
        var sum = 0L;

        for (var cell = 0; (cell < cellCount); cell++) {
            sum += lattice.Value(cell: cell, field: field).Value;
        }

        return sum;
    }
    private static long[] Snapshot(WorldFieldLattice lattice, int field, int cellCount) {
        var values = new long[cellCount];

        for (var cell = 0; (cell < cellCount); cell++) {
            values[cell] = lattice.Value(cell: cell, field: field).Value;
        }

        return values;
    }

    [Fact]
    public void ConservationHoldsExactlyOverManyStepsWhenNoClampBindsAndNoSpillIsDeclared() {
        const int width = 3;
        const int depth = 3;
        const int cells = (width * depth);
        var water = new int[cells];
        var ground = new int[cells];

        for (var z = 0; (z < depth); z++) {
            for (var x = 0; (x < width); x++) {
                var cell = ((z * width) + x);

                water[cell] = ((x + (3 * z)) % 5);
                ground[cell] = ((2 * x) + z);
            }
        }

        var lattice = Fixtures.BuildLattice(document: FlowFields(width: width, depth: depth));

        lattice.Restore(checkpoint: IntCheckpoint(water, ground));

        var before = SumRaw(cellCount: cells, field: 0, lattice: lattice);

        for (var step = 0; (step < 20); step++) {
            Fixtures.StepLattice(lattice: lattice);
        }

        var after = SumRaw(cellCount: cells, field: 0, lattice: lattice);

        Assert.Equal(actual: after, expected: before);
    }
    [Fact]
    public void FlatTerrainLevelsAWaterSpikeWithTheGlobalMaxMinGapNeverIncreasing() {
        const int width = 4;
        const int cells = width;

        var lattice = Fixtures.BuildLattice(document: FlowFields(width: width, depth: 1));

        lattice.Restore(checkpoint: IntCheckpoint([10, 0, 0, 0], [0, 0, 0, 0]));

        var values = Snapshot(cellCount: cells, field: 0, lattice: lattice);
        var gap = (values.Max() - values.Min());

        for (var step = 0; (step < 12); step++) {
            Fixtures.StepLattice(lattice: lattice);

            var next = Snapshot(cellCount: cells, field: 0, lattice: lattice);
            var nextGap = (next.Max() - next.Min());

            Assert.True(condition: (nextGap <= gap), userMessage: $"step {step}: gap grew from {gap} to {nextGap}.");
            Assert.Equal(expected: FixedQ4816.FromInteger(value: 10).Value, actual: next.Sum());

            gap = nextGap;
        }

        Assert.True(condition: (gap < FixedQ4816.FromInteger(value: 10).Value));
    }
    [Fact]
    public void ARampTerrainMovesMassStrictlyTowardTheLowEnd() {
        const int width = 4;

        // waterHeightScale 0 isolates the terrain-driven direction from water's own self-leveling contribution.
        var lattice = Fixtures.BuildLattice(document: FlowFields(width: width, depth: 1, waterHeightScale: 0f));

        lattice.Restore(checkpoint: IntCheckpoint([8, 0, 0, 0], [6, 4, 2, 0]));

        long WeightedPosition() {
            var sum = 0L;

            for (var x = 0; (x < width); x++) {
                sum += (x * lattice.Value(cell: x, field: 0).Value);
            }

            return sum;
        }

        var position = WeightedPosition();

        for (var step = 0; (step < 3); step++) {
            Fixtures.StepLattice(lattice: lattice);

            var next = WeightedPosition();

            Assert.True(condition: (next >= position), userMessage: $"step {step}: weighted position fell from {position} to {next}.");
            position = next;
        }

        Assert.True(condition: (position > 0L));
    }
    [Fact]
    public void TwoIdenticallyConstructedLatticesStayBitIdenticalAcrossSteps() {
        const int width = 3;
        const int depth = 3;
        const int cells = (width * depth);
        var checkpoint = IntCheckpoint(
            [4, 1, 2, 0, 3, 1, 2, 4, 0],
            [1, 3, 0, 2, 1, 4, 0, 2, 3]
        );

        var a = Fixtures.BuildLattice(document: FlowFields(width: width, depth: depth));
        var b = Fixtures.BuildLattice(document: FlowFields(width: width, depth: depth));

        a.Restore(checkpoint: checkpoint);
        b.Restore(checkpoint: checkpoint);

        for (var step = 0; (step < 15); step++) {
            Fixtures.StepLattice(lattice: a);
            Fixtures.StepLattice(lattice: b);

            Assert.Equal(expected: Snapshot(cellCount: cells, field: 0, lattice: b), actual: Snapshot(cellCount: cells, field: 0, lattice: a));
        }
    }
    [Fact]
    public void AnEdgeCellSpillsExactlyItsShareIntoSpillRowAndTheWholeSystemStillConserves() {
        var document = FlowFields(width: 2, depth: 1, waterHeightScale: 0f, includeGround: false, spillRow: "spill");
        var lattice = Fixtures.BuildLattice(
            document: document,
            state: [new WorldStateRow(Name: CellName.Parse(candidate: "spill"), Kind: CellKind.Fixed)]
        );

        lattice.Restore(checkpoint: IntCheckpoint([4, 6]));

        var flow = Assert.IsType<WorldFieldNode.Flow>(@object: Assert.Single(collection: lattice.Program.Nodes));
        WorldStateHandle written = default;
        var spilled = FixedQ4816.Zero;
        var calls = 0;

        Fixtures.StepLattice(lattice: lattice, host: new Fixtures.LambdaHost(addScalar: (row, amount, _) => {
            written = row;
            spilled = amount;
            calls++;
        }));

        // Flat height (waterHeightScale 0, no terrain): the two cells never pair-flow. Each spills its own
        // rate * value / directionCount share off the lattice edge -- 4/2 = 2 from cell 0, 6/2 = 3 from cell 1.
        Assert.Equal(actual: calls, expected: 1);
        Assert.Equal(expected: flow.SpillRow, actual: written);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 5), actual: spilled);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 2), actual: lattice.Value(cell: 0, field: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 1, field: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 10).Value, actual: (SumRaw(cellCount: 2, field: 0, lattice: lattice) + spilled.Value));
    }
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
