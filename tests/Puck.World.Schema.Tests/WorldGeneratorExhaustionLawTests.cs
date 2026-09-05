using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for the exhausting modes on a weighted numeric source — the numeric shuffle bag — and for the compiled
/// alias-table cache behind every weighted draw: drawing is exact per pass, replays from the persisted cursor and
/// mask, refuses or restarts by declaration, and the cache never changes what a source picks.
/// </summary>
public sealed class WorldGeneratorExhaustionLawTests {
    private const string Instance = "instance-alpha";
    private const string Site = "state.loot";
    private const ulong WorldSeed = 0x0123_4567_89AB_CDEFUL;

    private static WorldGenerator Bag(WorldGeneratorMode mode) => new(
        Source: WorldGeneratorSource.WeightedNumeric,
        Mode: mode,
        Weighted: [
            new WorldGeneratorWeightedNumeric(Value: 10, Weight: 1UL),
            new WorldGeneratorWeightedNumeric(Value: 20, Weight: 3UL),
            new WorldGeneratorWeightedNumeric(Value: 30, Weight: 5UL),
            new WorldGeneratorWeightedNumeric(Value: 40, Weight: 2UL),
        ]
    );
    private static bool TryFire(WorldGenerator generator, long cursor, IReadOnlyList<ClosedBitset256>? masks, out WorldGeneratorEngine.FireResult result, out string reason) =>
        WorldGeneratorEngine.TryFire(
            generator: generator,
            targetKind: CellKind.Int,
            seedState: WorldGeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: Site, worldSeed: WorldSeed),
            stream: WorldGeneratorEngine.ComputeStreamId(site: Site),
            cursor: cursor,
            masks: masks,
            result: out result,
            reason: out reason
        );
    private static WorldGeneratorEngine.FireResult Fire(WorldGenerator generator, long cursor, IReadOnlyList<ClosedBitset256>? masks) {
        Assert.True(condition: TryFire(generator: generator, cursor: cursor, masks: masks, result: out var result, reason: out var reason), userMessage: reason);

        return result;
    }
    private static string Validate(WorldGenerator generator, CellKind kind, WorldDrawTiming timing) {
        var definition = new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "loot"),
                    Kind: kind,
                    Draw: new WorldDraw(Generator: generator, Timing: timing)
                ),
            ])
        );

        return (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    }

    [Fact]
    public void WithoutReplacement_DrawsEveryOutcomeOncePerPass_ThenRefusesByName() {
        var bag = Bag(mode: WorldGeneratorMode.WithoutReplacement);
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;
        var drawn = new List<long>();

        for (var draw = 0; (draw < 4); draw++) {
            var fired = Fire(generator: bag, cursor: cursor, masks: masks);

            Assert.Equal(expected: 1L, actual: fired.Samples);
            Assert.NotNull(@object: fired.Masks);
            Assert.Single(collection: fired.Masks!);
            Assert.Equal(expected: (draw + 1), actual: fired.Masks![0].Count);

            drawn.Add(item: fired.Numeric!.Value);
            cursor += fired.Samples;
            masks = fired.Masks;
        }

        Assert.Equal(expected: new long[] { 10L, 20L, 30L, 40L }, actual: drawn.Order().ToArray());
        Assert.False(condition: TryFire(generator: bag, cursor: cursor, masks: masks, result: out _, reason: out var reason));
        Assert.Contains(expectedSubstring: "drawn out", actualString: reason);
    }
    [Fact]
    public void RestartOnExhaustion_StartsANewPass_AndEveryPassIsAPermutation() {
        var bag = Bag(mode: WorldGeneratorMode.RestartOnExhaustion);
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;

        for (var pass = 0; (pass < 5); pass++) {
            var drawn = new List<long>();

            for (var draw = 0; (draw < 4); draw++) {
                var fired = Fire(generator: bag, cursor: cursor, masks: masks);

                drawn.Add(item: fired.Numeric!.Value);
                cursor += fired.Samples;
                masks = fired.Masks;
            }

            Assert.Equal(expected: new long[] { 10L, 20L, 30L, 40L }, actual: drawn.Order().ToArray());
            Assert.Equal(expected: 4, actual: masks![0].Count);
        }
    }
    [Fact]
    public void PersistedCursorAndMask_ReplayTheSameDraw() {
        var bag = Bag(mode: WorldGeneratorMode.RestartOnExhaustion);
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;
        var trail = new List<(long Cursor, ClosedBitset256[]? Masks, long Value)>();

        for (var draw = 0; (draw < 11); draw++) {
            var fired = Fire(generator: bag, cursor: cursor, masks: masks);

            trail.Add(item: (cursor, masks?.ToArray(), fired.Numeric!.Value));
            cursor += fired.Samples;
            masks = fired.Masks;
        }

        foreach (var (at, mask, value) in trail) {
            Assert.Equal(expected: value, actual: Fire(generator: bag, cursor: at, masks: mask).Numeric);
        }
    }
    [Fact]
    public void WithReplacement_IgnoresAnyMaskAndPersistsNone() {
        var bag = Bag(mode: WorldGeneratorMode.WithReplacement);
        var plain = Fire(generator: bag, cursor: 7L, masks: null);
        var withStaleMask = Fire(generator: bag, cursor: 7L, masks: [new(Word0: 0b1011UL)]);

        Assert.Null(@object: plain.Masks);
        Assert.Equal(expected: plain.Numeric, actual: withStaleMask.Numeric);
    }
    [Fact]
    public void TheCompiledTableCache_NeverChangesWhatASourcePicks() {
        // The same declaration as two instances (one fresh, one a copy through `with`) draws the identical sequence
        // as one instance drawn twice: the cache is keyed by instance, and the table is a pure function of the
        // declaration, so neither the first build nor a later hit can move a pick.
        var first = Bag(mode: WorldGeneratorMode.WithReplacement);
        var second = (first with { });
        var third = Bag(mode: WorldGeneratorMode.WithReplacement);

        for (var cursor = 0L; (cursor < 64L); cursor++) {
            var expected = Fire(generator: first, cursor: cursor, masks: null).Numeric;

            Assert.Equal(expected: expected, actual: Fire(generator: first, cursor: cursor, masks: null).Numeric);
            Assert.Equal(expected: expected, actual: Fire(generator: second, cursor: cursor, masks: null).Numeric);
            Assert.Equal(expected: expected, actual: Fire(generator: third, cursor: cursor, masks: null).Numeric);
        }
    }
    [Fact]
    public void Validator_AdmitsModeOnTheExhaustingShapes_AndRefusesItElsewhere() {
        Assert.Equal(expected: string.Empty, actual: Validate(generator: Bag(mode: WorldGeneratorMode.RestartOnExhaustion), kind: CellKind.Int, timing: WorldDrawTiming.Event));
        Assert.Equal(expected: string.Empty, actual: Validate(generator: Bag(mode: WorldGeneratorMode.WithoutReplacement), kind: CellKind.Fixed, timing: WorldDrawTiming.Event));

        var uniform = new WorldGenerator(Source: WorldGeneratorSource.UniformRange, Mode: WorldGeneratorMode.RestartOnExhaustion, RangeMin: 0, RangeMax: 9);
        var stream = new WorldGenerator(Source: WorldGeneratorSource.StreamDraw, Mode: WorldGeneratorMode.WithoutReplacement);

        Assert.Contains(expectedSubstring: "only markov, weightedNumeric and symmetryOrbit exhaust", actualString: Validate(generator: uniform, kind: CellKind.Int, timing: WorldDrawTiming.Event));
        Assert.Contains(expectedSubstring: "only markov, weightedNumeric and symmetryOrbit exhaust", actualString: Validate(generator: stream, kind: CellKind.Int, timing: WorldDrawTiming.Event));

        // A boot-timed state row draws once at first fill and keeps its facet, so an exhausting mode is admitted
        // there the same way it is for a Markov source; only the settle-and-clear document fields refuse an
        // exhausting source.
        Assert.Equal(expected: string.Empty, actual: Validate(generator: Bag(mode: WorldGeneratorMode.RestartOnExhaustion), kind: CellKind.Int, timing: WorldDrawTiming.Boot));
    }
    [Fact]
    public void PartiallyDrawnSampling_IsAliasTableIdentical_WithoutPerDrawTableAllocation() {
        var generator = Bag(mode: WorldGeneratorMode.RestartOnExhaustion);
        const ulong Mask = 0b0101UL;
        var seed = WorldGeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: Site, worldSeed: WorldSeed);
        var stream = WorldGeneratorEngine.ComputeStreamId(site: Site);
        var weights = new ulong[] { 1UL, 3UL, 5UL, 2UL };

        for (var mask = 1UL; (mask < 0b1111UL); mask++) {
            var remaining = Enumerable.Range(start: 0, count: 4)
                .Where(predicate: index => ((mask & (1UL << index)) == 0UL))
                .Select(selector: index => (Element: index, Weight: weights[index]))
                .ToArray();

            for (var cursor = 0L; (cursor < 32L); cursor++) {
                var expectedRng = Pcg32XshRr.Create(state: seed, stream: stream);

                expectedRng.Advance(count: unchecked(((ulong)cursor) * 2UL));
                var expectedEntry = WeightedSampler.Create<int>(entries: remaining).Sample(generator: ref expectedRng);
                var actual = Fire(generator: generator, cursor: cursor, masks: [new(Word0: mask)]);

                Assert.Equal(expected: generator.Weighted![expectedEntry].Value, actual: actual.Numeric);
            }
        }

        _ = Fire(generator: generator, cursor: 0L, masks: [new(Word0: Mask)]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var cursor = 0L; (cursor < 1024L); cursor++) {
            _ = Fire(generator: generator, cursor: cursor, masks: [new(Word0: Mask)]);
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        // One tiny returned mask plus the test's one-element input mask are expected; rebuilding an AliasTable per
        // draw formerly allocated several arrays and exceeded this bound by orders of magnitude.
        Assert.InRange(actual: allocated, low: 0L, high: (256L * 1024L));
    }
    [Fact]
    public void ValidatorAndEngine_RefuseAnUndefinedMode() {
        var invalid = Bag(mode: unchecked((WorldGeneratorMode)byte.MaxValue));

        Assert.Contains(expectedSubstring: "is not a defined WorldGeneratorMode", actualString: Validate(generator: invalid, kind: CellKind.Int, timing: WorldDrawTiming.Event));
        Assert.False(condition: TryFire(generator: invalid, cursor: 0L, masks: null, result: out _, reason: out var reason));
        Assert.Contains(expectedSubstring: "is not a defined WorldGeneratorMode", actualString: reason);
    }
}
