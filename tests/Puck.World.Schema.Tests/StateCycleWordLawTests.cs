using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <see cref="WorldStateCycle"/> whose generator is an authored word — the loop's period is the
/// word's order, the rotation outputs read that order's root of unity, the lattice outputs walk the word's orbit, the
/// validator refuses a word that loops nothing and a power that is the identity, a word survives serialization and
/// record equality letter for letter, and a settled projection still reads the same bits on reload.
/// </summary>
public sealed class StateCycleWordLawTests {
    private static WorldDefinition BuildDefinition(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow SlotRow(string name, CellKind kind, long value, WorldStateCycle cycle) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: kind,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)],
        Cycle: cycle
    );
    private static long Read(WorldDefinition definition, string row, ulong tick) {
        Assert.True(condition: WorldStateReader.TryRead(definition: definition, key: null, rawValue: out var raw, row: out _, rowName: row, text: out _, tick: tick));

        return raw!.Value;
    }
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    // A word of the requested order, found by a deterministic search over ascending subsets of the eight seed mirrors:
    // each such word is the Coxeter element of a sub-diagram, so the orders reachable are the Coxeter numbers of the
    // sub-diagrams (twelve for E6, eighteen for E7, thirty for E8 itself). The order asserted is measured, not assumed.
    private static int[] WordOfOrder(int order) {
        for (var mask = 1; (mask < (1 << SymmetryWord.MaximumLength)); mask++) {
            var letters = new List<int>(capacity: SymmetryWord.MaximumLength);

            for (var mirror = 0; (mirror < SymmetryWord.MaximumLength); mirror++) {
                if ((mask & (1 << mirror)) != 0) { letters.Add(item: mirror); }
            }

            if (SymmetryWord.Create(mirrors: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: letters)).Order == order) {
                return [.. letters];
            }
        }

        throw new InvalidOperationException(message: $"no ascending seed-mirror word of order {order}");
    }

    [Fact]
    public void AWordsOrder_IsTheLoopsPeriod_OnEveryRotationOutput() {
        foreach (var order in new[] { 12, 18 }) {
            var letters = WordOfOrder(order: order);
            var word = SymmetryWord.Create(mirrors: letters);

            foreach (var power in new[] { 1, 5, -1 }) {
                var step = BuildDefinition(SlotRow(name: "s", kind: CellKind.Int, value: 3L, cycle: new WorldStateCycle(Word: letters, Power: power, TicksPerStep: 2)));
                var turns = BuildDefinition(SlotRow(name: "t", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Word: letters, Power: power, Output: WorldCycleOutput.Turns, TicksPerStep: 2)));
                var cos = BuildDefinition(SlotRow(name: "c", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Word: letters, Power: power, Output: WorldCycleOutput.Cos, TicksPerStep: 2)));
                var sin = BuildDefinition(SlotRow(name: "n", kind: CellKind.Fixed, value: 0L, cycle: new WorldStateCycle(Word: letters, Power: power, Output: WorldCycleOutput.Sin, TicksPerStep: 2)));

                Assert.Equal(expected: order, actual: new WorldStateCycle(Word: letters).Order);
                Assert.Equal(expected: string.Empty, actual: Validate(definition: step));

                for (var tick = 0UL; (tick < (ulong)(3 * order * 2)); tick++) {
                    var steps = (long)(tick / 2UL);
                    var index = (int)((power * (steps % order)) + 3L).FloorModulo(modulus: (long)order);

                    Assert.Equal(expected: index, actual: Read(definition: step, row: "s", tick: tick));
                    Assert.Equal(expected: ((((long)(index - 3).FloorModulo(modulus: order)) << FixedQ4816.FractionBitCount) / order), actual: Read(definition: turns, row: "t", tick: tick));
                    Assert.Equal(expected: CyclicRotation.Rotor(step: (index - 3), order: order).Real.Value, actual: Read(definition: cos, row: "c", tick: tick));
                    Assert.Equal(expected: CyclicRotation.Rotor(step: (index - 3), order: order).Imaginary.Value, actual: Read(definition: sin, row: "n", tick: tick));
                }

                // One full loop returns the phase.
                Assert.Equal(expected: 3L, actual: Read(definition: step, row: "s", tick: (ulong)(order * 2)));
                Assert.Equal(expected: word.Order, actual: order);
            }
        }
    }
    [Fact]
    public void LatticeOutputs_WalkTheWordsOrbit_AndRingMovesWhenTheOrbitCrossesRings() {
        var letters = WordOfOrder(order: 12);
        var word = SymmetryWord.Create(mirrors: letters);
        var seed = 5;
        var node = BuildDefinition(SlotRow(name: "n", kind: CellKind.Int, value: seed, cycle: new WorldStateCycle(Word: letters, Output: WorldCycleOutput.Node)));
        var ring = BuildDefinition(SlotRow(name: "r", kind: CellKind.Int, value: seed, cycle: new WorldStateCycle(Word: letters, Output: WorldCycleOutput.Ring)));
        var x = BuildDefinition(SlotRow(name: "x", kind: CellKind.Fixed, value: ((long)seed << FixedQ4816.FractionBitCount), cycle: new WorldStateCycle(Word: letters, Output: WorldCycleOutput.ProjectionX)));
        var ringsVisited = new HashSet<long>();

        for (var tick = 0UL; (tick < 36UL); tick++) {
            var expected = word.Apply(node: seed, steps: (long)tick);

            Assert.Equal(expected: expected, actual: Read(definition: node, row: "n", tick: tick));
            Assert.Equal(expected: SymmetryLattice.Ring(node: expected), actual: Read(definition: ring, row: "r", tick: tick));
            Assert.Equal(expected: SymmetryLattice.Project(node: expected).X.Value, actual: Read(definition: x, row: "x", tick: tick));
            ringsVisited.Add(item: Read(definition: ring, row: "r", tick: tick));
        }

        Assert.Equal(expected: (long)seed, actual: Read(definition: node, row: "n", tick: (ulong)word.OrbitLength(node: seed)));

        // The lattice's own cycle never leaves a ring; a word that is not a power of it can, and the test only asserts
        // what the word it found actually does.
        var coxeterRing = BuildDefinition(SlotRow(name: "r", kind: CellKind.Int, value: seed, cycle: new WorldStateCycle(Output: WorldCycleOutput.Ring, Power: 7)));

        for (var tick = 0UL; (tick < 60UL); tick++) {
            Assert.Equal(expected: SymmetryLattice.Ring(node: seed), actual: Read(definition: coxeterRing, row: "r", tick: tick));
        }

        var seedOrbitCrossesRings = false;

        for (var step = 0; (step < word.OrbitLength(node: seed)); step++) {
            seedOrbitCrossesRings |= (SymmetryLattice.Ring(node: word.Apply(node: seed, steps: step)) != SymmetryLattice.Ring(node: seed));
        }

        Assert.Equal(expected: seedOrbitCrossesRings, actual: (ringsVisited.Count > 1));
    }
    [Fact]
    public void Validator_RefusesAWordThatLoopsNothing_AndAnIdentityPower() {
        static string Refusal(WorldStateCycle cycle) => Validate(definition: BuildDefinition(SlotRow(name: "r", kind: CellKind.Int, value: 0L, cycle: cycle)));

        Assert.Contains(expectedSubstring: "moves no node", actualString: Refusal(cycle: new WorldStateCycle(Word: [3, 3])));
        Assert.Contains(expectedSubstring: "word holds 0 letters", actualString: Refusal(cycle: new WorldStateCycle(Word: [])));
        Assert.Contains(expectedSubstring: "word holds 9 letters", actualString: Refusal(cycle: new WorldStateCycle(Word: [0, 1, 2, 3, 4, 5, 6, 7, 0])));
        Assert.Contains(expectedSubstring: "word[1] 240 is not a symmetry-lattice node", actualString: Refusal(cycle: new WorldStateCycle(Word: [0, 240])));
        Assert.Contains(expectedSubstring: "word[0] -1 is not a symmetry-lattice node", actualString: Refusal(cycle: new WorldStateCycle(Word: [-1])));
        Assert.Contains(expectedSubstring: ".cycle.power 0 is the identity", actualString: Refusal(cycle: new WorldStateCycle(Word: [0, 2], Power: 0)));
        Assert.Contains(expectedSubstring: ".cycle.power 3 is outside the generator's order 3", actualString: Refusal(cycle: new WorldStateCycle(Word: [0, 2], Power: 3)));
        Assert.Contains(expectedSubstring: ".cycle.power -3 is outside the generator's order 3", actualString: Refusal(cycle: new WorldStateCycle(Word: [0, 2], Power: -3)));
        Assert.Equal(expected: string.Empty, actual: Refusal(cycle: new WorldStateCycle(Word: [0, 2], Power: -2)));
        Assert.Equal(expected: string.Empty, actual: Refusal(cycle: new WorldStateCycle(Power: 29)));
        Assert.Equal(expected: string.Empty, actual: Refusal(cycle: new WorldStateCycle(Power: -13)));
    }
    [Fact]
    public void AWord_RoundTripsThroughSerialization_AndCompares_LetterForLetter() {
        var letters = WordOfOrder(order: 12);
        var row = SlotRow(name: "dial", kind: CellKind.Int, value: 2L, cycle: new WorldStateCycle(Word: letters, Power: 5, TicksPerStep: 3));
        var definition = BuildDefinition(row);
        var json = System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.Contains(expectedSubstring: $"\"word\":[{string.Join(separator: ',', values: letters)}]", actualString: json.Replace(oldValue: " ", newValue: string.Empty).Replace(oldValue: "\n", newValue: string.Empty).Replace(oldValue: "\r", newValue: string.Empty));
        Assert.DoesNotContain(expectedSubstring: "plane", actualString: json);

        var restored = WorldDefinitionRows.FindStateRow(rows: WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(definition: definition)).State, name: "dial")!;

        Assert.Equal(expected: row.Cycle, actual: restored.Cycle);
        Assert.Equal(expected: row.Cycle!.GetHashCode(), actual: restored.Cycle!.GetHashCode());
        Assert.Equal(expected: new WorldStateCycle(Word: [.. letters], Power: 5, TicksPerStep: 3), actual: row.Cycle);
        Assert.NotEqual(expected: new WorldStateCycle(Word: [letters[1], letters[0], letters[2]], Power: 5, TicksPerStep: 3), actual: row.Cycle);
        Assert.NotEqual(expected: new WorldStateCycle(Power: 5, TicksPerStep: 3), actual: row.Cycle);

        // The default generator serializes with no word member at all.
        var plain = System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: BuildDefinition(SlotRow(name: "spin", kind: CellKind.Int, value: 0L, cycle: new WorldStateCycle(Power: 7)))));

        Assert.DoesNotContain(expectedSubstring: "\"word\"", actualString: plain);
        Assert.Contains(expectedSubstring: "\"power\": 7", actualString: plain);
    }
    [Fact]
    public void SettledPhase_UnderAWord_PreservesTheValueAndNextTransition() {
        var letters = WordOfOrder(order: 18);

        foreach (var output in new[] { WorldCycleOutput.Step, WorldCycleOutput.Node }) {
            var cycle = new WorldStateCycle(Word: letters, Power: 5, Output: output, EpochTick: 3, TicksPerStep: 4);
            var liveRow = SlotRow(name: "r", kind: CellKind.Int, value: 9L, cycle: cycle);
            var live = BuildDefinition(liveRow);
            var settledAt = 205UL;
            var settled = BuildDefinition(SlotRow(
                name: "r",
                kind: CellKind.Int,
                value: cycle.SettledPhase(baseValue: 9L, currentTick: settledAt, row: liveRow),
                cycle: (cycle with { EpochTick = 0, SubstepTicks = cycle.SettledSubstep(currentTick: settledAt) })
            ));

            for (var elapsed = 0UL; (elapsed < 30UL); elapsed++) {
                Assert.Equal(expected: Read(definition: live, row: "r", tick: (settledAt + elapsed)), actual: Read(definition: settled, row: "r", tick: elapsed));
            }
        }
    }
}
