using Puck.Maths;
using Puck.Physics.Fields;
using Puck.State;

namespace Puck.Physics.Tests;

/// <summary>Pins the mass-conserving <see cref="FieldReactionInput.Flow"/> transport reaction: exact conservation
/// (including boundary spill), equilibrium under flat terrain, directional movement under a ramp, and determinism —
/// the kernel-only half of the flow contract; the authoring vocabulary's own <c>over</c>/<c>spillRow</c> validator
/// laws stay in <c>tests/Puck.World.Tests</c>.</summary>
public sealed class FieldLatticeFlowLawTests {
    private static StateHandle Row(string name, CellKind kind = CellKind.Fixed) {
        var catalog = StateCatalog.Compile(section: new StateSection(Rows: [new StateRow(Name: CellName.Parse(candidate: name), Kind: kind)]));

        _ = catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle);

        return handle;
    }
    // Field 0 ("water", transported) and, when includeGround, field 1 ("ground", static terrain the reaction's
    // Over names). Both fields carry a [0, 100] envelope -- generous enough that the small whole-number test
    // fixtures below never bind a clamp (exact conservation depends on that).
    private static FieldLatticeInput FlowFields(
        int width,
        int depth,
        int layers = 1,
        FixedQ4816? waterHeightScale = null,
        bool includeGround = true,
        FixedQ4816? groundHeightScale = null,
        FixedQ4816? rate = null,
        StateHandle spillRow = default
    ) {
        var fields = new List<FieldDescriptorInput> {
            new(Name: "water", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 100), HeightScale: (waterHeightScale ?? FixedQ4816.One), IsMedium: false, Color: "#3B7BD6"),
        };
        List<int>? over = null;

        if (includeGround) {
            fields.Add(item: new FieldDescriptorInput(Name: "ground", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 100), HeightScale: (groundHeightScale ?? FixedQ4816.One), IsMedium: false, Color: "#808080"));
            over = [1];
        }

        return new FieldLatticeInput(
            Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: width, Depth: depth, Layers: layers, StepEveryTicks: 1),
            Fields: fields,
            Reactions: [new FieldReactionInput.Flow(Field: 0, Rate: new FieldScalarInput(Literal: (rate ?? FixedQ4816.One), State: default), Over: (IReadOnlyList<int>?)over ?? [], SpillRow: spillRow)],
            Paint: []
        );
    }
    private static FieldLattice.Checkpoint IntCheckpoint(params int[][] fieldsRaw) => new(
        Raw: [.. fieldsRaw.Select(selector: static values => values.Select(selector: static v => FixedQ4816.FromInteger(value: v).Value).ToArray())]
    );
    private static long SumRaw(FieldLattice lattice, int field, int cellCount) {
        var sum = 0L;

        for (var cell = 0; (cell < cellCount); cell++) {
            sum += lattice.Value(cell: cell, field: field).Value;
        }

        return sum;
    }
    private static long[] Snapshot(FieldLattice lattice, int field, int cellCount) {
        var values = new long[cellCount];

        for (var cell = 0; (cell < cellCount); cell++) {
            values[cell] = lattice.Value(cell: cell, field: field).Value;
        }

        return values;
    }
    private static void StepLattice(FieldLattice lattice) => lattice.Step(tick: 1, bodyCount: 0, host: new NoopHost());
    private sealed class NoopHost : IFieldLatticeHost {
        public FixedVector3? BodyPosition(int body) => null;
        public long ReadTag(StateHandle row, int body, ulong tick) => 0L;
        public void WriteTag(StateHandle row, int body, long value, ulong tick) { }
        public FixedQ4816 ReadScalar(StateHandle row, ulong tick) => FixedQ4816.Zero;
        public void AddScalar(StateHandle row, FixedQ4816 amount, ulong tick) { }
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

        var lattice = new FieldLattice(input: FlowFields(width: width, depth: depth));

        lattice.Restore(checkpoint: IntCheckpoint(water, ground));

        var before = SumRaw(cellCount: cells, field: 0, lattice: lattice);

        for (var step = 0; (step < 20); step++) {
            StepLattice(lattice: lattice);
        }

        var after = SumRaw(cellCount: cells, field: 0, lattice: lattice);

        Assert.Equal(actual: after, expected: before);
    }
    [Fact]
    public void FlatTerrainLevelsAWaterSpikeWithTheGlobalMaxMinGapNeverIncreasing() {
        const int width = 4;
        const int cells = width;

        var lattice = new FieldLattice(input: FlowFields(width: width, depth: 1));

        lattice.Restore(checkpoint: IntCheckpoint([10, 0, 0, 0], [0, 0, 0, 0]));

        var values = Snapshot(cellCount: cells, field: 0, lattice: lattice);
        var gap = (values.Max() - values.Min());

        for (var step = 0; (step < 12); step++) {
            StepLattice(lattice: lattice);

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
        var lattice = new FieldLattice(input: FlowFields(width: width, depth: 1, waterHeightScale: FixedQ4816.Zero));

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
            StepLattice(lattice: lattice);

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

        var a = new FieldLattice(input: FlowFields(width: width, depth: depth));
        var b = new FieldLattice(input: FlowFields(width: width, depth: depth));

        a.Restore(checkpoint: checkpoint);
        b.Restore(checkpoint: checkpoint);

        for (var step = 0; (step < 15); step++) {
            StepLattice(lattice: a);
            StepLattice(lattice: b);

            Assert.Equal(expected: Snapshot(cellCount: cells, field: 0, lattice: b), actual: Snapshot(cellCount: cells, field: 0, lattice: a));
        }
    }
    [Fact]
    public void AnEdgeCellSpillsExactlyItsShareIntoSpillRowAndTheWholeSystemStillConserves() {
        var spillRow = Row(name: "spill");
        var input = FlowFields(width: 2, depth: 1, waterHeightScale: FixedQ4816.Zero, includeGround: false, spillRow: spillRow);
        var lattice = new FieldLattice(input: input);

        lattice.Restore(checkpoint: IntCheckpoint([4, 6]));

        var flow = Assert.IsType<FieldReactionInput.Flow>(@object: Assert.Single(collection: lattice.Input.Reactions));
        StateHandle written = default;
        var spilled = FixedQ4816.Zero;
        var calls = 0;

        lattice.Step(tick: 1, bodyCount: 0, host: new SpillHost((row, amount) => {
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
    private sealed class SpillHost(Action<StateHandle, FixedQ4816> onAddScalar) : IFieldLatticeHost {
        public FixedVector3? BodyPosition(int body) => null;
        public long ReadTag(StateHandle row, int body, ulong tick) => 0L;
        public void WriteTag(StateHandle row, int body, long value, ulong tick) { }
        public FixedQ4816 ReadScalar(StateHandle row, ulong tick) => FixedQ4816.Zero;
        public void AddScalar(StateHandle row, FixedQ4816 amount, ulong tick) => onAddScalar(row, amount);
    }
}
