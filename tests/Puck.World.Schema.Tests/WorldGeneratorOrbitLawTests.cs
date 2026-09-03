using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for the symmetry-orbit source: its cards are the ring's thirty nodes or a node's orbit under a word, a deck
/// mode deals each card once per pass, the validator refuses every shape that names no single orbit, a site's domain
/// must admit node indices, a persisted deck must fit the orbit, and a lattice fill needs enough undealt cards.
/// </summary>
public sealed class WorldGeneratorOrbitLawTests {
    private const string Instance = "instance-alpha";
    private const string Site = "state.slot";
    private const ulong WorldSeed = 0x0123_4567_89AB_CDEFUL;

    private static WorldGenerator RingSource(int ring, WorldGeneratorMode mode = WorldGeneratorMode.WithReplacement) => new(
        Source: WorldGeneratorSource.SymmetryOrbit,
        Mode: mode,
        Ring: ring
    );
    private static WorldGenerator NodeSource(int node, IReadOnlyList<int>? word, WorldGeneratorMode mode = WorldGeneratorMode.WithReplacement) => new(
        Source: WorldGeneratorSource.SymmetryOrbit,
        Mode: mode,
        Node: node,
        Word: word
    );
    private static bool TryFire(WorldGenerator generator, long cursor, IReadOnlyList<ClosedBitset256>? decks, out WorldGeneratorEngine.FireResult result, out string reason) =>
        WorldGeneratorEngine.TryFire(
            generator: generator,
            targetKind: CellKind.Int,
            seedState: WorldGeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: Site, worldSeed: WorldSeed),
            stream: WorldGeneratorEngine.ComputeStreamId(site: Site),
            cursor: cursor,
            decks: decks,
            result: out result,
            reason: out reason
        );
    private static string Validate(WorldGenerator generator, CellKind kind = CellKind.Int, long? max = null, IReadOnlyList<ClosedBitset256>? decks = null) {
        var definition = new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "slot"),
                    Kind: kind,
                    Max: max,
                    Min: ((max is null) ? null : 0L),
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)],
                    Draw: new WorldDraw(Generator: generator, Timing: WorldDrawTiming.Event),
                    DrawDecks: decks
                ),
            ])
        );

        return (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    }

    [Fact]
    public void ARingSource_DealsEveryNodeOfTheRingOncePerPass() {
        var source = RingSource(ring: 2, mode: WorldGeneratorMode.WithoutReplacement);
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? decks = null;
        var dealt = new List<long>();

        Assert.True(condition: WorldGeneratorEngine.TryResolveOrbit(generator: source, nodes: out var nodes, reason: out var orbitReason), userMessage: orbitReason);
        Assert.Equal(expected: SymmetryLattice.RingSize, actual: nodes.Length);

        for (var deal = 0; (deal < SymmetryLattice.RingSize); deal++) {
            Assert.True(condition: TryFire(generator: source, cursor: cursor, decks: decks, result: out var fired, reason: out var reason), userMessage: reason);
            Assert.Equal(expected: 1L, actual: fired.Samples);
            Assert.Equal(expected: 2, actual: SymmetryLattice.Ring(node: (int)fired.Numeric!.Value));
            dealt.Add(item: fired.Numeric!.Value);
            cursor += fired.Samples;
            decks = fired.Decks;
        }

        Assert.Equal(expected: nodes.Order().Select(selector: static node => (long)node).ToArray(), actual: dealt.Order().ToArray());
        Assert.False(condition: TryFire(generator: source, cursor: cursor, decks: decks, result: out _, reason: out var exhausted));
        Assert.Contains(expectedSubstring: "orbit is dealt out", actualString: exhausted);

        // With replacement the same cursor replays the same node, and every draw stays on the ring.
        var replacing = RingSource(ring: 2);

        for (var probe = 0L; (probe < 50L); probe++) {
            Assert.True(condition: TryFire(generator: replacing, cursor: probe, decks: null, result: out var first, reason: out _));
            Assert.True(condition: TryFire(generator: replacing, cursor: probe, decks: null, result: out var again, reason: out _));
            Assert.Equal(expected: first.Numeric, actual: again.Numeric);
            Assert.Equal(expected: 2, actual: SymmetryLattice.Ring(node: (int)first.Numeric!.Value));
            Assert.Null(@object: first.Decks);
        }
    }
    [Fact]
    public void ANodeSource_DrawsTheOrbitUnderItsWord_OrTheNodesRingWithoutOne() {
        int[] letters = [0, 1, 2, 3, 4, 5];
        var word = SymmetryWord.Create(mirrors: letters);
        var source = NodeSource(node: 5, word: letters, mode: WorldGeneratorMode.ReshuffleOnExhaustion);

        Assert.True(condition: WorldGeneratorEngine.TryResolveOrbit(generator: source, nodes: out var nodes, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: word.OrbitLength(node: 5), actual: nodes.Length);

        for (var step = 0; (step < nodes.Length); step++) {
            Assert.Equal(expected: word.Apply(node: 5, steps: step), actual: nodes[step]);
        }

        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? decks = null;
        var seen = new HashSet<long>();

        for (var deal = 0; (deal < (2 * nodes.Length)); deal++) {
            Assert.True(condition: TryFire(generator: source, cursor: cursor, decks: decks, result: out var fired, reason: out var fireReason), userMessage: fireReason);
            Assert.Contains(expected: (int)fired.Numeric!.Value, collection: nodes);
            seen.Add(item: fired.Numeric!.Value);
            cursor += fired.Samples;
            decks = fired.Decks;
        }

        Assert.Equal(expected: nodes.Length, actual: seen.Count);

        Assert.True(condition: WorldGeneratorEngine.TryResolveOrbit(generator: NodeSource(node: 17, word: null), nodes: out var ringNodes, reason: out _));
        Assert.Equal(expected: SymmetryLattice.RingSize, actual: ringNodes.Length);
        Assert.All(collection: ringNodes, action: node => Assert.Equal(expected: SymmetryLattice.Ring(node: 17), actual: SymmetryLattice.Ring(node: node)));
        Assert.Equal(expected: 17, actual: ringNodes[0]);
    }
    [Fact]
    public void Validator_RefusesEveryShapeThatNamesNoSingleOrbit() {
        Assert.Equal(expected: string.Empty, actual: Validate(generator: RingSource(ring: 0)));
        Assert.Equal(expected: string.Empty, actual: Validate(generator: NodeSource(node: 239, word: [7, 6])));
        Assert.Contains(expectedSubstring: "neither 'ring' nor 'node'", actualString: Validate(generator: new WorldGenerator(Source: WorldGeneratorSource.SymmetryOrbit)));
        Assert.Contains(expectedSubstring: "both 'ring' and 'node'", actualString: Validate(generator: new WorldGenerator(Source: WorldGeneratorSource.SymmetryOrbit, Ring: 1, Node: 4)));
        Assert.Contains(expectedSubstring: "'word' beside 'ring'", actualString: Validate(generator: new WorldGenerator(Source: WorldGeneratorSource.SymmetryOrbit, Ring: 1, Word: [0])));
        Assert.Contains(expectedSubstring: "ring 8 is not", actualString: Validate(generator: RingSource(ring: 8)));
        Assert.Contains(expectedSubstring: "node 240 is not", actualString: Validate(generator: NodeSource(node: 240, word: null)));
        Assert.Contains(expectedSubstring: "word holds 9 letters", actualString: Validate(generator: NodeSource(node: 3, word: [0, 1, 2, 3, 4, 5, 6, 7, 0])));
        Assert.Contains(expectedSubstring: "beside start/contexts/rangeMin/rangeMax/weighted", actualString: Validate(generator: new WorldGenerator(Source: WorldGeneratorSource.SymmetryOrbit, Ring: 1, RangeMin: 0, RangeMax: 4)));
        Assert.Contains(expectedSubstring: "beside ring/node/word, which belong to source=symmetryOrbit", actualString: Validate(generator: new WorldGenerator(Source: WorldGeneratorSource.UniformRange, RangeMin: 0, RangeMax: 4, Ring: 1)));
        Assert.Contains(expectedSubstring: "writes a numeric value, but the site is kind=text", actualString: Validate(generator: RingSource(ring: 0), kind: CellKind.Text));
        Assert.Contains(expectedSubstring: "outside the site's admissible domain", actualString: Validate(generator: RingSource(ring: 0), max: 100L));
        Assert.Equal(expected: string.Empty, actual: Validate(generator: RingSource(ring: 0), max: 239L));

        // A persisted deck must fit the orbit, and a non-dealing mode carries none.
        Assert.Equal(expected: string.Empty, actual: Validate(generator: RingSource(ring: 0, mode: WorldGeneratorMode.WithoutReplacement), decks: [new(Word0: 0b101UL)]));
        Assert.Contains(expectedSubstring: "marks a card past the 30", actualString: Validate(generator: RingSource(ring: 0, mode: WorldGeneratorMode.WithoutReplacement), decks: [new(Word0: (1UL << 30))]));
        Assert.Contains(expectedSubstring: "never deals", actualString: Validate(generator: RingSource(ring: 0), decks: [new(Word0: 1UL)]));
        Assert.Contains(expectedSubstring: "exactly one", actualString: Validate(generator: RingSource(ring: 0, mode: WorldGeneratorMode.WithoutReplacement), decks: [new(Word0: 1UL), new(Word0: 2UL)]));
    }
    [Fact]
    public void ALatticeFill_NeedsEnoughUndealtCards() {
        var source = RingSource(ring: 3, mode: WorldGeneratorMode.WithoutReplacement);

        Assert.True(condition: WorldGeneratorEngine.TryCheckBatchCapacity(generator: source, decks: null, sampleCount: 30L, reason: out _));
        Assert.False(condition: WorldGeneratorEngine.TryCheckBatchCapacity(generator: source, decks: null, sampleCount: 31L, reason: out var reason));
        Assert.Contains(expectedSubstring: "can supply only 30", actualString: reason);
        Assert.False(condition: WorldGeneratorEngine.TryCheckBatchCapacity(generator: source, decks: [new(Word0: 0b111UL)], sampleCount: 28L, reason: out _));
        Assert.True(condition: WorldGeneratorEngine.TryCheckBatchCapacity(generator: source, decks: [new(Word0: 0b111UL)], sampleCount: 27L, reason: out _));
        Assert.True(condition: WorldGeneratorEngine.TryCheckBatchCapacity(generator: RingSource(ring: 3, mode: WorldGeneratorMode.ReshuffleOnExhaustion), decks: null, sampleCount: 90L, reason: out _));
    }
}
