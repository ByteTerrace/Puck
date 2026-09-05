using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for entry multiplicities on drawn entry sets and for the whole-field batch draw a lattice row's <c>draw</c>
/// paint takes: a multiplicity is exactly that many units per pass, a batch is the site's own stream cell by cell,
/// and the validator admits one numeric draw fill per lattice row and nothing else.
/// </summary>
public sealed class WorldLatticeDrawLawTests {
    private const string Instance = "instance-alpha";
    private const string Site = "state.tiles";
    private const ulong WorldSeed = 0x0BAD_F00D_1234_5678UL;

    private static WorldGenerator CountedBag(WorldGeneratorMode mode) => new(
        Source: WorldGeneratorSource.WeightedNumeric,
        Mode: mode,
        Weighted: [
            new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL, Multiplicity: 2),
            new WorldGeneratorWeightedNumeric(Value: 2, Weight: 1UL),
            new WorldGeneratorWeightedNumeric(Value: 3, Weight: 4UL, Multiplicity: 3),
        ]
    );
    private static (ulong Seed, ulong Stream) Keys() => (
        WorldGeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: Site, worldSeed: WorldSeed),
        WorldGeneratorEngine.ComputeStreamId(site: Site)
    );
    private static WorldGeneratorEngine.FireResult Fire(WorldGenerator generator, long cursor, IReadOnlyList<ClosedBitset256>? masks) {
        var (seed, stream) = Keys();

        Assert.True(condition: WorldGeneratorEngine.TryFire(generator: generator, targetKind: CellKind.Fixed, seedState: seed, stream: stream, cursor: cursor, masks: masks, result: out var result, reason: out var reason), userMessage: reason);

        return result;
    }
    private static string Validate(WorldStateSection state, IReadOnlyList<WorldGeneratorRow>? generators = null) {
        var definition = new WorldDefinition(
            Generators: generators,
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: state
        );

        return (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    }
    private static WorldStateSection LatticeState(WorldLatticeFill? fill, long cursor = 0L, IReadOnlyList<ClosedBitset256>? masks = null, WorldLatticeFill? second = null) {
        var paint = new List<WorldLatticeFill>();

        if (fill is not null) { paint.Add(item: fill); }
        if (second is not null) { paint.Add(item: second); }

        return new WorldStateSection(
            World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "tiles"),
                    Kind: CellKind.Fixed,
                    DrawCursor: cursor,
                    DrawnMasks: masks,
                    Domain: new WorldStateDomain.CellsOf(Topology: "grid"),
                    Field: new WorldStateFieldTrait(Max: 4f, Paint: ((paint.Count == 0) ? null : paint))
                ),
            ],
            Lattices: [
                new WorldStateLatticeTopology.Field(Name: "grid", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 4, Depth: 1, Layers: 1, StepEveryTicks: 1),
            ]
        );
    }

    [Fact]
    public void AMultiplicity_IsExactlyThatManyUnitsPerPass() {
        var bag = CountedBag(mode: WorldGeneratorMode.RestartOnExhaustion);
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;

        for (var pass = 0; (pass < 4); pass++) {
            var drawn = new List<long>();

            for (var draw = 0; (draw < 6); draw++) {
                var fired = Fire(generator: bag, cursor: cursor, masks: masks);

                drawn.Add(item: fired.Numeric!.Value);
                cursor += fired.Samples;
                masks = fired.Masks;
            }

            Assert.Equal(expected: new long[] { 1L, 1L, 2L, 3L, 3L, 3L }, actual: drawn.Order().ToArray());
        }

        var once = CountedBag(mode: WorldGeneratorMode.WithoutReplacement);
        var (seed, stream) = Keys();
        cursor = 0L;
        masks = null;

        for (var draw = 0; (draw < 6); draw++) {
            var fired = Fire(generator: once, cursor: cursor, masks: masks);

            cursor += fired.Samples;
            masks = fired.Masks;
        }

        Assert.False(condition: WorldGeneratorEngine.TryFire(generator: once, targetKind: CellKind.Fixed, seedState: seed, stream: stream, cursor: cursor, masks: masks, result: out _, reason: out var reason));
        Assert.Contains(expectedSubstring: "drawn out (6 units", actualString: reason);
    }
    [Fact]
    public void AMultipliedMarkovContext_DrawsItsUnitsOncePerPass() {
        var walk = new WorldGenerator(
            Source: WorldGeneratorSource.Markov,
            Start: WorldCellName.Parse(candidate: "bag"),
            Mode: WorldGeneratorMode.WithoutReplacement,
            Contexts: [
                new WorldGeneratorContext(Key: WorldCellName.Parse(candidate: "bag"), Alternatives: [
                    new WorldGeneratorAlternative(Token: "a", Weight: 1UL, Next: WorldCellName.Parse(candidate: "end"), Multiplicity: 3),
                    new WorldGeneratorAlternative(Token: "b", Weight: 1UL, Next: WorldCellName.Parse(candidate: "end")),
                ]),
                new WorldGeneratorContext(Key: WorldCellName.Parse(candidate: "end")),
            ]
        );
        var (seed, stream) = Keys();
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;
        var drawn = new List<string>();

        for (var draw = 0; (draw < 4); draw++) {
            Assert.True(condition: WorldGeneratorEngine.TryFire(generator: walk, targetKind: CellKind.Text, seedState: seed, stream: stream, cursor: cursor, masks: masks, result: out var fired, reason: out var reason), userMessage: reason);

            drawn.Add(item: fired.Text!);
            cursor += fired.Samples;
            masks = fired.Masks;
        }

        Assert.Equal(expected: new[] { "a", "a", "a", "b" }, actual: drawn.Order(comparer: StringComparer.Ordinal).ToArray());
        Assert.False(condition: WorldGeneratorEngine.TryFire(generator: walk, targetKind: CellKind.Text, seedState: seed, stream: stream, cursor: cursor, masks: masks, result: out _, reason: out _));
    }
    [Fact]
    public void ABatch_IsTheSitesOwnStream_CellByCell() {
        var (seed, stream) = Keys();

        foreach (var generator in new[] { CountedBag(mode: WorldGeneratorMode.RestartOnExhaustion), CountedBag(mode: WorldGeneratorMode.WithReplacement), new WorldGenerator(Source: WorldGeneratorSource.UniformRange, RangeMin: -5, RangeMax: 5), new WorldGenerator(Source: WorldGeneratorSource.StreamDraw) }) {
            var cells = new long[13];

            Assert.True(condition: WorldGeneratorEngine.TryFireBatch(generator: generator, targetKind: CellKind.Fixed, seedState: seed, stream: stream, cursor: 7L, masks: null, values: cells, masksAfter: out var masksAfter, reason: out var reason), userMessage: reason);
            Assert.True(condition: WorldGeneratorEngine.TryAdvanceBatch(generator: generator, targetKind: CellKind.Fixed, seedState: seed, stream: stream, cursor: 7L, masks: null, sampleCount: cells.Length, masksAfter: out var advancedMasks, reason: out var advanceReason), userMessage: advanceReason);

            var cursor = 7L;
            IReadOnlyList<ClosedBitset256>? masks = null;

            for (var cell = 0; (cell < cells.Length); cell++) {
                var single = Fire(generator: generator, cursor: cursor, masks: masks);

                Assert.Equal(expected: single.Numeric, actual: cells[cell]);
                cursor += single.Samples;
                masks = single.Masks;
            }

            Assert.Equal(expected: masks, actual: masksAfter);
            Assert.Equal(expected: masksAfter, actual: advancedMasks);
        }

        var text = new WorldGenerator(Source: WorldGeneratorSource.Markov, Start: WorldCellName.Parse(candidate: "x"), Contexts: [new WorldGeneratorContext(Key: WorldCellName.Parse(candidate: "x"))]);

        Assert.False(condition: WorldGeneratorEngine.TryFireBatch(generator: text, targetKind: CellKind.Fixed, seedState: seed, stream: stream, cursor: 0L, masks: null, values: new long[2], masksAfter: out _, reason: out var textReason));
        Assert.Contains(expectedSubstring: "cannot fill cells", actualString: textReason);
    }
    [Fact]
    public void Validator_AdmitsOneNumericDrawFillPerLatticeRow() {
        var numeric = new WorldLatticeFill.Draw(Generator: CountedBag(mode: WorldGeneratorMode.RestartOnExhaustion));
        var markov = new WorldLatticeFill.Draw(Generator: new WorldGenerator(Source: WorldGeneratorSource.Markov, Start: WorldCellName.Parse(candidate: "x"), Contexts: [new WorldGeneratorContext(Key: WorldCellName.Parse(candidate: "x"))]));
        var named = new WorldLatticeFill.Draw(Source: WorldCellName.Parse(candidate: "loot"));
        var generators = new[] { new WorldGeneratorRow(Name: WorldCellName.Parse(candidate: "loot"), Generator: CountedBag(mode: WorldGeneratorMode.WithoutReplacement)) };

        Assert.Equal(expected: string.Empty, actual: Validate(state: LatticeState(fill: numeric)));
        Assert.Equal(expected: string.Empty, actual: Validate(state: LatticeState(fill: named, cursor: 8L, masks: [new(Word0: 0b101UL)]), generators: generators));
        Assert.Contains(expectedSubstring: "writes text", actualString: Validate(state: LatticeState(fill: markov)));
        Assert.Contains(expectedSubstring: "names no declared generator", actualString: Validate(state: LatticeState(fill: named)));
        Assert.Contains(expectedSubstring: "second draw fill", actualString: Validate(state: LatticeState(fill: numeric, second: numeric)));
        Assert.Contains(expectedSubstring: "drawCursor without draw", actualString: Validate(state: LatticeState(fill: null, cursor: 4L)));
    }
    [Fact]
    public void Validator_RefusesBadMultiplicities_AndTooManyUnits() {
        static string Refusal(WorldGenerator generator) => Validate(state: new WorldStateSection(World: [
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "loot"), Kind: CellKind.Int, Draw: new WorldDraw(Generator: generator, Timing: WorldDrawTiming.Event)),
        ]));

        Assert.Contains(expectedSubstring: ".multiplicity 0 must be at least 1", actualString: Refusal(new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric, Weighted: [new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL, Multiplicity: 0)])));
        Assert.Contains(expectedSubstring: "holds 257 units", actualString: Refusal(new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric, Weighted: [new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL, Multiplicity: 257)])));
        Assert.Equal(expected: string.Empty, actual: Refusal(new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric, Weighted: [new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL, Multiplicity: 256)])));
        var oversized = new WorldGenerator(Source: WorldGeneratorSource.WeightedNumeric, Weighted: [
            new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL, Multiplicity: int.MaxValue),
            new WorldGeneratorWeightedNumeric(Value: 2, Weight: 1UL, Multiplicity: int.MaxValue),
        ]);

        Assert.Contains(expectedSubstring: "4294967294 units", actualString: Refusal(oversized));
        Assert.False(condition: WorldGeneratorEngine.TryFire(generator: oversized, targetKind: CellKind.Int, seedState: 1UL, stream: 1UL, cursor: 0L, masks: null, result: out _, reason: out var oversizedReason));
        Assert.Contains(expectedSubstring: "4294967294 units", actualString: oversizedReason);
    }
    [Fact]
    public void Validator_RefusesAWithoutReplacementPassThatCannotFillTheLattice() {
        var oneOutcome = new WorldGenerator(
            Source: WorldGeneratorSource.WeightedNumeric,
            Mode: WorldGeneratorMode.WithoutReplacement,
            Weighted: [new WorldGeneratorWeightedNumeric(Value: 1, Weight: 1UL)]
        );
        var refusal = Validate(state: LatticeState(fill: new WorldLatticeFill.Draw(Generator: oneOutcome)));

        Assert.Contains(expectedSubstring: "can supply only 1 positive-weight undrawn unit", actualString: refusal);
        Assert.Contains(expectedSubstring: "lattice pass requires 4 samples", actualString: refusal);
    }
}
