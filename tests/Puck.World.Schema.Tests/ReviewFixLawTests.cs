using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws pinning the review corrections around the state traits and draw sites: the runtime cell validator refuses
/// every shape the boot walk refuses, a drive-gate row never carries a cycling cell, persisted dealt masks must fit
/// their source, a fractional comparand lowers to the exact integer gate, a rule's generate reaches a lattice draw
/// fill, dynamics state rides the fixed spelling on every row kind, and a motion row's ground-stick bias compiles
/// independently of the kit's own speed.
/// </summary>
public sealed class ReviewFixLawTests {
    private static WorldDefinition Definition(IReadOnlyList<WorldStateRow> rows, IReadOnlyList<WorldRule>? rules = null, IReadOnlyList<WorldGeneratorRow>? generators = null) => new(
        Generators: generators,
        Rules: rules,
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow Slot(string name, CellKind kind, long value, WorldStateAdvance? advance = null, WorldStateCycle? cycle = null) => new(
        Name: WorldCellName.Parse(candidate: name),
        Kind: kind,
        Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)],
        Advance: advance,
        Cycle: cycle
    );
    private static string Refusal(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);

    [Fact]
    public void RuntimeCellValidator_RefusesWhatTheBootWalkRefuses() {
        // A keyed cell minted beside a row-level advance: the boot walk refuses it, so the live door must too.
        var keyedBesideAdvance = Definition(rows: [
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "regen"),
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 10), new WorldStateCell(Key: WorldCellName.Parse(candidate: "1"), Value: 5)],
                Advance: new WorldStateAdvance(RateDenominator: 1, RateNumerator: 1)
            ),
        ]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateRuntimeStateCell(definition: keyedBesideAdvance, rowName: "regen", key: "1", reason: out var advanceReason));
        Assert.Contains(expectedSubstring: "declares advance on a keyed row", actualString: advanceReason);

        // A phase outside the lattice on a node-output cycle row.
        var badNode = Definition(rows: [Slot(name: "spin", kind: CellKind.Int, value: 240, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node))]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateRuntimeStateCell(definition: badNode, rowName: "spin", key: WorldStateRow.SlotKey.Value, reason: out var nodeReason));
        Assert.Contains(expectedSubstring: "is not a symmetry-lattice node", actualString: nodeReason);

        // The well-formed write still passes.
        var fine = Definition(rows: [Slot(name: "spin", kind: CellKind.Int, value: 17, cycle: new WorldStateCycle(Output: WorldCycleOutput.Node))]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateRuntimeStateCell(definition: fine, rowName: "spin", key: WorldStateRow.SlotKey.Value, reason: out var fineReason), userMessage: fineReason);
    }
    [Fact]
    public void ADriveGateRow_NeverCarriesACyclingCell() {
        var gate = Definition(rows: [
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "gate"),
                Kind: CellKind.Int,
                Capacity: 4,
                GatesDrive: true,
                Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 0, Cycle: new WorldStateCycle())]
            ),
        ]);

        Assert.Contains(expectedSubstring: "declares cycle on a gatesDrive row", actualString: Refusal(definition: gate));
    }
    [Fact]
    public void PersistedDecks_MustFitTheSitesSource() {
        static WorldStateRow Site(WorldGenerator generator, IReadOnlyList<ClosedBitset256>? decks) => new(
            Name: WorldCellName.Parse(candidate: "loot"),
            Kind: CellKind.Int,
            Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 1)],
            Draw: new WorldDraw(Generator: generator, Timing: WorldDrawTiming.Event),
            DrawDecks: decks
        );
        var bag = new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric, Mode: WorldGeneratorMode.ReshuffleOnExhaustion, Weighted: [new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL), new WorldGeneratorWeightedNumeric(Value: 2, Weight: 1UL)]);
        var plain = new WorldGenerator(Source: WorldGeneratorSource.UniformRange, RangeMin: 0, RangeMax: 9);

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: Definition(rows: [Site(generator: bag, decks: [new(Word0: 0b11UL)])])));
        Assert.Contains(expectedSubstring: "exactly one", actualString: Refusal(definition: Definition(rows: [Site(generator: bag, decks: [new(Word0: 5UL), new(Word0: 0UL), new(Word0: 0UL)])])));
        Assert.Contains(expectedSubstring: "marks a card past the 2", actualString: Refusal(definition: Definition(rows: [Site(generator: bag, decks: [new(Word0: 0b101UL)])])));
        Assert.Contains(expectedSubstring: "never deals", actualString: Refusal(definition: Definition(rows: [Site(generator: plain, decks: [new(Word0: 1UL)])])));

        // The engine sheds masks a non-dealing source cannot own, so a re-authored site self-heals on its next draw.
        Assert.Null(@object: WorldGeneratorEngine.DecksAfter(generator: plain, fired: null, previous: [new(Word0: 1)]));
        Assert.Equal(expected: new ClosedBitset256[] { new(Word0: 3) }, actual: WorldGeneratorEngine.DecksAfter(generator: bag, fired: [new(Word0: 3)], previous: [new(Word0: 1)]));
        Assert.Equal(expected: new ClosedBitset256[] { new(Word0: 1) }, actual: WorldGeneratorEngine.DecksAfter(generator: bag, fired: null, previous: [new(Word0: 1)]));
    }
    [Fact]
    public void AFractionalComparand_LowersToTheExactIntegerGate() {
        static CompiledWorldPredicate Compile(ActionStateComparison comparison, decimal literal, CellKind kind = CellKind.Int) {
            var definition = Definition(
                rows: [Slot(name: "count", kind: kind, value: 2)],
                rules: [new WorldRule(Name: WorldCellName.Parse(candidate: "probe"), Effects: [new ActionEffect.SetState(State: "count", Value: 1m)], Gate: new ActionPredicate.CompareState(State: "count", Comparison: comparison, Value: literal), Mode: ActionTriggerMode.Edge)]
            );

            return WorldRuleCompiler.CompileAll(definition: definition)[0].Gate[0];
        }

        var greater = Compile(comparison: ActionStateComparison.Greater, literal: 1.5m);

        Assert.Equal(expected: ActionStateComparison.GreaterOrEqual, actual: greater.Comparison);
        Assert.Equal(expected: 2L, actual: greater.Value);

        var greaterOrEqual = Compile(comparison: ActionStateComparison.GreaterOrEqual, literal: 0.5m);

        Assert.Equal(expected: ActionStateComparison.GreaterOrEqual, actual: greaterOrEqual.Comparison);
        Assert.Equal(expected: 1L, actual: greaterOrEqual.Value);

        var less = Compile(comparison: ActionStateComparison.Less, literal: 1.5m);

        Assert.Equal(expected: ActionStateComparison.LessOrEqual, actual: less.Comparison);
        Assert.Equal(expected: 1L, actual: less.Value);

        var equal = Compile(comparison: ActionStateComparison.Equal, literal: 1.5m);

        Assert.Equal(expected: ActionStateComparison.Greater, actual: equal.Comparison);
        Assert.Equal(expected: long.MaxValue, actual: equal.Value);

        var integral = Compile(comparison: ActionStateComparison.Greater, literal: 2m);

        Assert.Equal(expected: ActionStateComparison.Greater, actual: integral.Comparison);
        Assert.Equal(expected: 2L, actual: integral.Value);

        var fixedGate = Compile(comparison: ActionStateComparison.Greater, literal: 1.5m, kind: CellKind.Fixed);

        Assert.Equal(expected: ActionStateComparison.Greater, actual: fixedGate.Comparison);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 1.5).Value, actual: fixedGate.Value);
    }
    [Fact]
    public void ARuleGenerate_ReachesALatticeDrawFill() {
        var lattice = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "tiles"),
            Kind: CellKind.Fixed,
            Lattice: new WorldStateLatticeTrait(Topology: "grid", Max: 4f, Paint: [new WorldLatticeFill.Draw(Generator: new WorldGenerator(Source: WorldGeneratorSource.UniformRange, RangeMin: 0, RangeMax: 65536))])
        );
        var definition = new WorldDefinition(
            Rules: [new WorldRule(Name: WorldCellName.Parse(candidate: "redraw"), Effects: [new ActionEffect.Generate(Row: "tiles")], Mode: ActionTriggerMode.Edge)],
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(
                World: [lattice],
                Lattices: [new WorldStateLatticeTopology(Name: "grid", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 4, Depth: 1, Layers: 1, StepEveryTicks: 1)]
            )
        );

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: definition));
        Assert.Equal(expected: WorldRuleEffectKind.Generate, actual: WorldRuleCompiler.CompileAll(definition: definition)[0].Effects[0].Kind);
    }
    [Fact]
    public void DynamicsState_RoundTripsInTheFixedSpelling_OnAnIntRow() {
        var row = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 300)],
            Dynamics: new WorldStateDynamics(Row: "critical", Y0: (300L << FixedQ4816.FractionBitCount), V0: (5L << FixedQ4816.FractionBitCount), EpochTick: 7)
        );
        var definition = new WorldDefinition(
            DynamicsRaw: [new WorldDynamicsRow(Damping: 1f, Frequency: 1f, Name: "critical", Response: 0f)],
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [row])
        );
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);
        var json = System.Text.Encoding.UTF8.GetString(bytes: bytes);

        Assert.Contains(expectedSubstring: "\"300\"", actualString: json);
        Assert.DoesNotContain(expectedSubstring: "19660800", actualString: json);

        var restored = WorldDefinitionSerialization.Deserialize(bytes);

        Assert.Equal(expected: row.Dynamics, actual: WorldDefinitionRows.FindStateRow(rows: restored.State, name: "gauge")!.Dynamics);
    }
    [Fact]
    public void GroundStick_CompilesIndependentlyOfSpeed_AndDefaultsToTheEngineConstant() {
        static FixedMotionTuning Compile(float speed, float? groundStick = null) => WorldMotionTuningFactory.Compile(
            tuning: new WorldMotion(
                Speed: new WorldSpeed(Value: speed),
                Turn: new WorldTurn(Rate: 1f),
                GroundStick: (groundStick ?? 2f)
            ),
            channels: WorldChannelTable.Empty,
            dynamics: [],
            simulationRateHz: 240
        );
        var slow = Compile(speed: 1f);
        var fast = Compile(speed: 50f);

        // A faster kit's own resolved move speed must never widen the surface catch-up bias: scaling it by speed
        // over-corrects a shallow slope climb (the bias converts to downhill drift under depenetration faster than
        // it converts to held contact).
        Assert.Equal(expected: slow.GroundStick, actual: fast.GroundStick);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 2d), actual: slow.GroundStick);

        var authored = Compile(speed: 1f, groundStick: 5f);

        Assert.Equal(expected: FixedQ4816.FromDouble(value: 5d), actual: authored.GroundStick);
    }
    [Fact]
    public void ObstructionGrace_CompilesDirectlyToExactEngineTicksWithoutFixedPointRounding() {
        var tuning = WorldMotionTuningFactory.Compile(
            tuning: new WorldMotion(
                Speed: new WorldSpeed(Value: 1f),
                Turn: new WorldTurn(Rate: 1f),
                ObstructionRaw: new WorldObstructionLatch(GraceSeconds: 0.1m)
            ),
            channels: WorldChannelTable.Empty,
            dynamics: [],
            simulationRateHz: 240
        );

        Assert.Equal(expected: 5_040UL, actual: tuning.Obstruction.GraceTicks);
    }
}
