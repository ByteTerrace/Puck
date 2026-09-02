using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldStateCycle"/> — the tick-indexed rotation trait <see cref="WorldStateReader.TryRead"/>
/// resolves for a slot row or a keyed cell. Every read is checked against <c>Puck.Maths.CyclicRotation</c> and
/// <c>Puck.Maths.SymmetryLattice</c> called directly, so the trait's own arithmetic (phase, ticks per step, epoch, the
/// modular reduction of a tick count) is what is under test; the validator's refusals close the shapes the read side
/// could not honestly compute over.
/// </summary>
public sealed class StateCycleReadLawTests {
    private static WorldDefinition BuildDefinition(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow SlotRow(string name, CellKind kind, long value, WorldStateCycle cycle, long? min = null, long? max = null) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: kind,
        Min: min,
        Max: max,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)],
        Cycle: cycle
    );
    private static long Read(WorldDefinition definition, string row, ulong tick, string? key = null) {
        Assert.True(condition: WorldStateReader.TryRead(definition: definition, key: key, rawValue: out var raw, row: out _, rowName: row, text: out _, tick: tick));
        Assert.NotNull(@object: raw);

        return raw!.Value;
    }
    // The validator's one refusal string (empty for a valid document), so a test names the refusal it expects.
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);

    [Fact]
    public void Step_ReadsThePowerTimesStepsPlusPhase_ModuloThePeriod() {
        foreach (var (plane, power) in new[] { (0, 1), (1, 7), (2, 11), (3, 13) }) {
            foreach (var phase in new long[] { 0L, 7L, -1L, 61L }) {
                var definition = BuildDefinition(SlotRow(name: "spin", kind: CellKind.Int, value: phase, cycle: new WorldStateCycle(Power: power)));

                foreach (var tick in new ulong[] { 0UL, 1UL, 29UL, 30UL, 31UL, 1000UL, 123456789UL }) {
                    var expected = (int)(((long)CyclicRotation.Step(plane: plane, tick: (long)(tick % 30UL))) + phase).FloorModulo(modulus: 30L);

                    Assert.Equal(expected: expected, actual: Read(definition: definition, row: "spin", tick: tick));
                }
            }
        }
    }
    [Fact]
    public void TicksPerStepAndEpoch_ScaleAndOffsetTheStepCount() {
        var definition = BuildDefinition(SlotRow(name: "spin", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(EpochTick: 100, Power: 1, TicksPerStep: 20)));

        Assert.Equal(expected: 0L, actual: Read(definition: definition, row: "spin", tick: 0UL));
        Assert.Equal(expected: 0L, actual: Read(definition: definition, row: "spin", tick: 119UL));
        Assert.Equal(expected: 1L, actual: Read(definition: definition, row: "spin", tick: 120UL));
        Assert.Equal(expected: 1L, actual: Read(definition: definition, row: "spin", tick: 139UL));
        Assert.Equal(expected: 29L, actual: Read(definition: definition, row: "spin", tick: (100UL + (29UL * 20UL))));
        Assert.Equal(expected: 0L, actual: Read(definition: definition, row: "spin", tick: (100UL + (30UL * 20UL))));
    }
    [Fact]
    public void FixedOutputs_ReadTheRotorTurnsAndComponents() {
        var turns = BuildDefinition(SlotRow(name: "t", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Turns)));
        var cos = BuildDefinition(SlotRow(name: "c", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Cos, Power: 11)));
        var sin = BuildDefinition(SlotRow(name: "s", kind: CellKind.Fixed, value: (3L << FixedQ4816.FractionBitCount), cycle: new WorldStateCycle(Output: WorldCycleOutput.Sin, Power: 13)));

        for (var tick = 0UL; (tick < 90UL); ++tick) {
            Assert.Equal(expected: ((((long)(tick % 30UL)) << FixedQ4816.FractionBitCount) / 30L), actual: Read(definition: turns, row: "t", tick: tick));
            Assert.Equal(expected: CyclicRotation.At(plane: 2, tick: (long)tick).Real.Value, actual: Read(definition: cos, row: "c", tick: tick));
            Assert.Equal(expected: CyclicRotation.Rotor(step: (CyclicRotation.Step(plane: 3, tick: (long)tick) + 3)).Imaginary.Value, actual: Read(definition: sin, row: "s", tick: tick));
        }
    }
    [Fact]
    public void LatticeOutputs_CarryThePhaseNodeAroundItsRing() {
        var node = BuildDefinition(SlotRow(name: "n", kind: CellKind.Int, value: 5L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node, Power: 7)));
        var x = BuildDefinition(SlotRow(name: "x", kind: CellKind.Fixed, value: (5L << FixedQ4816.FractionBitCount), cycle: new WorldStateCycle(Output: WorldCycleOutput.ProjectionX, Power: 7)));
        var y = BuildDefinition(SlotRow(name: "y", kind: CellKind.Fixed, value: (5L << FixedQ4816.FractionBitCount), cycle: new WorldStateCycle(Output: WorldCycleOutput.ProjectionY, Power: 7)));

        for (var tick = 0UL; (tick < 60UL); ++tick) {
            var expected = SymmetryLattice.Cycle(node: 5, steps: CyclicRotation.Step(plane: 1, tick: (long)tick));

            Assert.Equal(expected: expected, actual: Read(definition: node, row: "n", tick: tick));
            Assert.Equal(expected: SymmetryLattice.Project(node: expected).X.Value, actual: Read(definition: x, row: "x", tick: tick));
            Assert.Equal(expected: SymmetryLattice.Project(node: expected).Y.Value, actual: Read(definition: y, row: "y", tick: tick));
            Assert.Equal(expected: SymmetryLattice.Ring(node: 5), actual: SymmetryLattice.Ring(node: (int)Read(definition: node, row: "n", tick: tick)));
        }

        // Power 1 walks one node per step, so thirty steps close the ring on the phase node.
        var walk = BuildDefinition(SlotRow(name: "n", kind: CellKind.Int, value: 17L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node)));

        Assert.Equal(expected: 17L, actual: Read(definition: walk, row: "n", tick: 0UL));
        Assert.Equal(expected: SymmetryLattice.Cycle(node: 17), actual: Read(definition: walk, row: "n", tick: 1UL));
        Assert.Equal(expected: 17L, actual: Read(definition: walk, row: "n", tick: 30UL));
    }
    [Fact]
    public void KeyedCells_TurnIndependently_AndAPlainCellStaysStored() {
        var row = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "dials"),
            Kind: CellKind.Int,
            Cells: [
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "a"), Value: 0L, Cycle: new WorldStateCycle(Power: 1)),
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "b"), Value: 10L, Cycle: new WorldStateCycle(Power: 7, TicksPerStep: 2)),
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "c"), Value: 4L),
            ]
        );
        var definition = BuildDefinition(row);

        Assert.Equal(expected: string.Empty, actual: Validate(definition: definition));

        for (var tick = 0UL; (tick < 70UL); ++tick) {
            Assert.Equal(expected: CyclicRotation.Step(plane: 0, tick: (long)tick), actual: Read(definition: definition, key: "a", row: "dials", tick: tick));
            Assert.Equal(expected: (int)(((long)CyclicRotation.Step(plane: 1, tick: (long)(tick / 2UL))) + 10L).FloorModulo(modulus: 30L), actual: Read(definition: definition, key: "b", row: "dials", tick: tick));
            Assert.Equal(expected: 4L, actual: Read(definition: definition, key: "c", row: "dials", tick: tick));
        }
    }
    [Fact]
    public void Envelope_ClampsTheComputedValue_NeverTheStoredPhase() {
        var definition = BuildDefinition(SlotRow(name: "spin", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(Power: 1), max: 10L, min: 0L));

        Assert.Equal(expected: 9L, actual: Read(definition: definition, row: "spin", tick: 9UL));
        Assert.Equal(expected: 10L, actual: Read(definition: definition, row: "spin", tick: 25UL));
        Assert.Equal(expected: 1L, actual: Read(definition: definition, row: "spin", tick: 31UL));
    }
    [Fact]
    public void SettledPhase_PreservesTheCurrentValueAndNextTransition() {
        foreach (var output in new[] { WorldCycleOutput.Step, WorldCycleOutput.Node }) {
            var cycle = new WorldStateCycle(EpochTick: 3, Output: output, Power: 11, TicksPerStep: 4);
            var live = BuildDefinition(SlotRow(name: "r", kind: CellKind.Int, value: 9L, cycle: cycle));
            var liveRow = SlotRow(name: "r", kind: CellKind.Int, value: 9L, cycle: cycle);
            var settledAt = 205UL;
            var settled = BuildDefinition(SlotRow(
                name: "r",
                kind: CellKind.Int,
                value: cycle.SettledPhase(baseValue: 9L, currentTick: settledAt, row: liveRow),
                cycle: (cycle with { EpochTick = 0, SubstepTicks = cycle.SettledSubstep(currentTick: settledAt) })
            ));

            for (var elapsed = 0UL; elapsed < 12UL; elapsed++) {
                Assert.Equal(
                    expected: Read(definition: live, row: "r", tick: (settledAt + elapsed)),
                    actual: Read(definition: settled, row: "r", tick: elapsed)
                );
            }
        }
    }
    [Fact]
    public void SettledPhase_OnAFixedRow_RidesTheRowsEncoding() {
        var cycle = new WorldStateCycle(Output: WorldCycleOutput.ProjectionX, Power: 7, TicksPerStep: 3);
        var row = SlotRow(name: "x", kind: CellKind.Fixed, value: (5L << FixedQ4816.FractionBitCount), cycle: cycle);
        var liveValue = Read(definition: BuildDefinition(row), row: "x", tick: 77UL);
        var settledRaw = cycle.SettledPhase(baseValue: row.Cells![0].Value, currentTick: 77UL, row: row);

        Assert.Equal(expected: 0L, actual: (settledRaw & ((1L << FixedQ4816.FractionBitCount) - 1L)));
        Assert.Equal(expected: liveValue, actual: Read(definition: BuildDefinition(SlotRow(name: "x", kind: CellKind.Fixed, value: settledRaw, cycle: (cycle with { EpochTick = 0 }))), row: "x", tick: 0UL));
    }
    [Fact]
    public void Validator_RefusesTheShapesTheReadSideCannotHonour() {
        static string Refusal(WorldStateRow row) => Validate(definition: BuildDefinition(row));

        Assert.Contains(expectedSubstring: "does not suit", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Turns))));
        Assert.Contains(expectedSubstring: "does not suit", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Step))));
        Assert.Contains(expectedSubstring: "only int/fixed cells turn", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Text, value: 0L, cycle: new WorldStateCycle())));
        Assert.Contains(expectedSubstring: ".cycle.power 30 is outside", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(Power: 30))));
        Assert.Contains(expectedSubstring: ".cycle.power 0 is the identity", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(Power: 0))));
        Assert.Contains(expectedSubstring: ".cycle.ticksPerStep 0", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(TicksPerStep: 0))));
        Assert.Contains(expectedSubstring: ".cycle.substepTicks", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(TicksPerStep: 4, SubstepTicks: 4))));
        Assert.Contains(expectedSubstring: ".cycle.epochTick", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(EpochTick: -1))));
        Assert.Contains(expectedSubstring: "is not a defined WorldCycleOutput", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Output: unchecked((WorldCycleOutput)byte.MaxValue)))));
        Assert.Contains(expectedSubstring: "is not a symmetry-lattice node", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 240L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node))));
        Assert.Contains(expectedSubstring: "declares both advance and cycle", actualString: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle()) with { Advance = new WorldStateAdvance(RateDenominator: 1, RateNumerator: 1) }));
        Assert.Contains(expectedSubstring: "declares cycle on a keyed row", actualString: Refusal(new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "r"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 0L)],
            Cycle: new WorldStateCycle()
        )));
        Assert.Contains(expectedSubstring: "declares cycle beside advance or dynamics", actualString: Refusal(new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "r"),
            Kind: CellKind.Int,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "k"), Value: 0L, Advance: new WorldStateAdvance(RateDenominator: 1, RateNumerator: 1), Cycle: new WorldStateCycle())]
        )));

        // The well-formed shapes validate clean, so the refusals above are not a validator that refuses everything.
        Assert.Equal(expected: string.Empty, actual: Refusal(SlotRow(name: "r", kind: CellKind.Int, value: 5L, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node, Power: 13, TicksPerStep: 8))));
        Assert.Equal(expected: string.Empty, actual: Refusal(SlotRow(name: "r", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Output: WorldCycleOutput.ProjectionY))));
    }
}
