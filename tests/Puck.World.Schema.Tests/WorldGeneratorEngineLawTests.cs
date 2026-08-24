using Puck.Maths;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for authored randomness' source x site x moment split. The source is a stateless declaration, the site
/// descriptor owns stream identity and cursor/decks, and timing controls when a site fires without becoming another
/// seed input.
/// </summary>
public sealed class WorldGeneratorEngineLawTests {
    private const ulong WorldSeed = 0x0123_4567_89AB_CDEFUL;
    private const string Instance = "instance-alpha";

    private static readonly WorldGenerator s_stream = new(Source: WorldGeneratorSource.StreamDraw);
    private static readonly WorldGenerator s_uniform = new(
        Source: WorldGeneratorSource.UniformRange,
        RangeMin: -17,
        RangeMax: 29
    );
    private static readonly WorldGenerator s_weighted = new(
        Source: WorldGeneratorSource.WeightedNumeric,
        Weighted: [
            new WorldGeneratorWeightedNumeric(Value: -11, Weight: 1UL),
            new WorldGeneratorWeightedNumeric(Value: 7, Weight: 3UL),
            new WorldGeneratorWeightedNumeric(Value: 101, Weight: 5UL),
        ]
    );
    private static readonly WorldGenerator s_twoTokenMarkov = new(
        Source: WorldGeneratorSource.Markov,
        Start: Name("start"),
        Bound: 2,
        Contexts: [
            new WorldGeneratorContext(
                Key: Name("start"),
                Alternatives: [
                    new WorldGeneratorAlternative(Token: "red", Weight: 1UL, Next: Name("tail")),
                    new WorldGeneratorAlternative(Token: "blue", Weight: 2UL, Next: Name("tail")),
                ]
            ),
            new WorldGeneratorContext(
                Key: Name("tail"),
                Alternatives: [
                    new WorldGeneratorAlternative(Token: "fox", Weight: 1UL, Next: Name("done")),
                    new WorldGeneratorAlternative(Token: "hare", Weight: 1UL, Next: Name("done")),
                ]
            ),
            new WorldGeneratorContext(Key: Name("done")),
        ]
    );

    [Fact]
    public void SourcesDeclareTheFixedAdvanceCostCursorSeekingDependsOn() {
        Assert.Equal(expected: 2UL, actual: WorldGeneratorEngine.AdvancesPerSample(source: WorldGeneratorSource.Markov));
        Assert.Equal(expected: 1UL, actual: WorldGeneratorEngine.AdvancesPerSample(source: WorldGeneratorSource.UniformRange));
        Assert.Equal(expected: 2UL, actual: WorldGeneratorEngine.AdvancesPerSample(source: WorldGeneratorSource.WeightedNumeric));
        Assert.Equal(expected: 1UL, actual: WorldGeneratorEngine.AdvancesPerSample(source: WorldGeneratorSource.StreamDraw));
    }

    [Fact]
    public void NumericSourcesSeekToTheSameSampleAsWalkingEveryPriorCursor() {
        foreach (var generator in (WorldGenerator[])[s_stream, s_uniform, s_weighted]) {
            for (var cursor = 0L; cursor < 32L; cursor++) {
                var fired = Fire(generator: generator, site: "state.loot", cursor: cursor);

                Assert.Equal(expected: 1L, actual: fired.Samples);
                Assert.Equal(
                    expected: NumericOracle(generator: generator, site: "state.loot", cursor: cursor),
                    actual: fired.Numeric
                );
            }
        }
    }

    [Fact]
    public void MarkovCursorCountsSamplesRatherThanEmissions() {
        var cursor = 0L;
        var walked = new List<(long Cursor, string Text)>();

        for (var emission = 0; emission < 16; emission++) {
            var fired = Fire(generator: s_twoTokenMarkov, site: "state.name", cursor: cursor);

            Assert.Equal(expected: 2L, actual: fired.Samples);
            Assert.Equal(expected: TwoTokenMarkovOracle(site: "state.name", cursor: cursor), actual: fired.Text);
            walked.Add(item: (cursor, fired.Text!));
            cursor += fired.Samples;
        }

        foreach (var expected in walked) {
            var seeked = Fire(generator: s_twoTokenMarkov, site: "state.name", cursor: expected.Cursor);

            Assert.Equal(expected: expected.Text, actual: seeked.Text);
            Assert.Equal(expected: 2L, actual: seeked.Samples);
        }
    }

    [Fact]
    public void TimingControlsTheMomentWithoutPerturbingTheSiteSequence() {
        var sourceName = Name("shared");
        var sources = (IReadOnlyList<WorldGeneratorRow>)[new WorldGeneratorRow(Name: sourceName, Generator: s_weighted)];

        foreach (var timing in Enum.GetValues<WorldDrawTiming>()) {
            var draw = new WorldDraw(Source: sourceName, Timing: timing);

            Assert.True(condition: WorldGeneratorEngine.TryResolveSource(
                generators: sources,
                draw: draw,
                generator: out var resolved,
                reason: out var reason
            ), userMessage: reason);

            var sequence = NumericSequence(generator: resolved, site: "state.reward", count: 24);

            Assert.Equal(expected: NumericSequence(generator: s_weighted, site: "state.reward", count: 24), actual: sequence);
        }
    }

    [Fact]
    public void SharingASourceSharesNoPositionBetweenSites() {
        var baseline = NumericSequence(generator: s_stream, site: "state.left", count: 32);

        _ = NumericSequence(generator: s_stream, site: "state.right", count: 128);
        var afterOtherSiteAdvanced = NumericSequence(generator: s_stream, site: "state.left", count: 32);

        Assert.Equal(expected: baseline, actual: afterOtherSiteAdvanced);
        Assert.False(
            condition: baseline.SequenceEqual(second: NumericSequence(generator: s_stream, site: "state.right", count: 32)),
            userMessage: "different site descriptors unexpectedly produced the same 32-sample stream"
        );
    }

    [Fact]
    public void ReorderingSitesDoesNotRepointTheirStreams() {
        var firstOrder = new[] { "state.alpha", "state.beta", "state.gamma" };
        var secondOrder = new[] { "state.gamma", "state.alpha", "state.beta" };
        var baseline = firstOrder.ToDictionary(
            keySelector: static site => site,
            elementSelector: site => NumericSequence(generator: s_uniform, site: site, count: 24),
            comparer: StringComparer.Ordinal
        );

        foreach (var site in secondOrder) {
            Assert.Equal(expected: baseline[site], actual: NumericSequence(generator: s_uniform, site: site, count: 24));
        }
    }

    [Fact]
    public void WorldSeedAndInstanceIdentityEachIsolateTheWholeSiteSequence() {
        var baseline = NumericSequence(generator: s_stream, site: "state.roll", count: 32);
        var otherWorldSeed = NumericSequence(generator: s_stream, site: "state.roll", count: 32, worldSeed: (WorldSeed + 1UL));
        var otherInstance = NumericSequence(generator: s_stream, site: "state.roll", count: 32, instance: "instance-beta");

        Assert.False(condition: baseline.SequenceEqual(second: otherWorldSeed), userMessage: "changing the world-seed rung left the 32-sample sequence unchanged");
        Assert.False(condition: baseline.SequenceEqual(second: otherInstance), userMessage: "changing the instance rung left the 32-sample sequence unchanged");
        Assert.False(condition: otherWorldSeed.SequenceEqual(second: otherInstance), userMessage: "the independently changed seed and instance rungs produced the same 32-sample sequence");
    }

    [Fact]
    public void PersistedCursorAndDeckReplayTheNextWithoutReplacementDeal() {
        var generator = DealGenerator(mode: WorldGeneratorMode.WithoutReplacement);
        var site = "state.card";
        var cursor = 0L;
        IReadOnlyList<long>? decks = null;
        var dealt = new List<string>();
        var snapshots = new List<(long Cursor, IReadOnlyList<long>? Decks)>();

        for (var deal = 0; deal < 3; deal++) {
            snapshots.Add(item: (cursor, decks?.ToArray()));
            var fired = Fire(generator: generator, site: site, cursor: cursor, decks: decks);

            dealt.Add(item: fired.Text!);
            cursor += fired.Samples;
            decks = fired.Decks;
        }

        Assert.Equal(expected: 3, actual: dealt.Distinct(comparer: StringComparer.Ordinal).Count());

        for (var deal = 0; deal < dealt.Count; deal++) {
            var snapshot = snapshots[deal];
            var replayed = Fire(generator: generator, site: site, cursor: snapshot.Cursor, decks: snapshot.Decks);

            Assert.Equal(expected: dealt[deal], actual: replayed.Text);
        }

        Assert.False(condition: TryFire(
            generator: generator,
            site: site,
            cursor: cursor,
            decks: decks,
            result: out _,
            reason: out var reason
        ));
        Assert.Contains(expectedSubstring: "dealt out", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ReshuffleOnExhaustionStartsANewDeterministicDeck() {
        var generator = DealGenerator(mode: WorldGeneratorMode.ReshuffleOnExhaustion);
        var site = "state.card";
        var cursor = 0L;
        IReadOnlyList<long>? decks = null;

        for (var deal = 0; deal < 3; deal++) {
            var fired = Fire(generator: generator, site: site, cursor: cursor, decks: decks);

            cursor += fired.Samples;
            decks = fired.Decks;
        }

        var reshuffled = Fire(generator: generator, site: site, cursor: cursor, decks: decks);
        var replayed = Fire(generator: generator, site: site, cursor: cursor, decks: decks?.ToArray());

        Assert.Equal(expected: reshuffled.Text, actual: replayed.Text);
        Assert.Equal(expected: reshuffled.Numeric, actual: replayed.Numeric);
        Assert.Equal(expected: reshuffled.Samples, actual: replayed.Samples);
        Assert.Equal(expected: reshuffled.Decks, actual: replayed.Decks);
        Assert.NotNull(@object: reshuffled.Decks);
        Assert.Equal(expected: 1, actual: System.Numerics.BitOperations.PopCount(value: unchecked((ulong)reshuffled.Decks![0])));
    }

    private static WorldCellName Name(string value) => WorldCellName.Parse(candidate: value);

    private static WorldGenerator DealGenerator(WorldGeneratorMode mode) => new(
        Source: WorldGeneratorSource.Markov,
        Start: Name("deck"),
        Contexts: [
            new WorldGeneratorContext(
                Key: Name("deck"),
                Alternatives: [
                    new WorldGeneratorAlternative(Token: "one", Weight: 1UL, Next: Name("done")),
                    new WorldGeneratorAlternative(Token: "two", Weight: 1UL, Next: Name("done")),
                    new WorldGeneratorAlternative(Token: "three", Weight: 1UL, Next: Name("done")),
                ]
            ),
            new WorldGeneratorContext(Key: Name("done")),
        ],
        Mode: mode
    );

    private static WorldGeneratorEngine.FireResult Fire(
        WorldGenerator generator,
        string site,
        long cursor,
        IReadOnlyList<long>? decks = null,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) {
        Assert.True(condition: TryFire(
            generator: generator,
            site: site,
            cursor: cursor,
            decks: decks,
            result: out var result,
            reason: out var reason,
            worldSeed: worldSeed,
            instance: instance
        ), userMessage: reason);

        return result;
    }

    private static bool TryFire(
        WorldGenerator generator,
        string site,
        long cursor,
        IReadOnlyList<long>? decks,
        out WorldGeneratorEngine.FireResult result,
        out string reason,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) => WorldGeneratorEngine.TryFire(
        generator: generator,
        targetKind: ((generator.Source == WorldGeneratorSource.Markov) ? CellKind.Text : CellKind.Int),
        seedState: WorldGeneratorEngine.ComputeSeedState(worldSeed: worldSeed, instanceIdentity: instance, site: site),
        stream: WorldGeneratorEngine.ComputeStreamId(site: site),
        cursor: cursor,
        decks: decks,
        result: out result,
        reason: out reason
    );

    private static long[] NumericSequence(
        WorldGenerator generator,
        string site,
        int count,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) {
        var values = new long[count];

        for (var cursor = 0L; cursor < count; cursor++) {
            values[cursor] = Fire(
                generator: generator,
                site: site,
                cursor: cursor,
                worldSeed: worldSeed,
                instance: instance
            ).Numeric!.Value;
        }

        return values;
    }

    private static long NumericOracle(WorldGenerator generator, string site, long cursor) {
        var rng = Pcg32XshRr.Create(
            state: WorldGeneratorEngine.ComputeSeedState(worldSeed: WorldSeed, instanceIdentity: Instance, site: site),
            stream: WorldGeneratorEngine.ComputeStreamId(site: site)
        );

        rng.Advance(count: unchecked(((ulong)cursor * WorldGeneratorEngine.AdvancesPerSample(source: generator.Source))));

        return generator.Source switch {
            WorldGeneratorSource.StreamDraw => rng.NextUInt32(),
            WorldGeneratorSource.UniformRange => (generator.RangeMin!.Value + (long)(((((ulong)(uint)(generator.RangeMax!.Value - generator.RangeMin.Value)) + 1UL) * rng.NextUnitFraction32().Value) >> 32)),
            WorldGeneratorSource.WeightedNumeric => WeightedSampler.Create<long>(entries: generator.Weighted!.Select(selector: static row => (row.Value, row.Weight)).ToArray()).Sample(generator: ref rng),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(generator)),
        };
    }

    private static string TwoTokenMarkovOracle(string site, long cursor) {
        var rng = Pcg32XshRr.Create(
            state: WorldGeneratorEngine.ComputeSeedState(worldSeed: WorldSeed, instanceIdentity: Instance, site: site),
            stream: WorldGeneratorEngine.ComputeStreamId(site: site)
        );

        rng.Advance(count: unchecked(((ulong)cursor * WorldGeneratorEngine.AdvancesPerSample(source: WorldGeneratorSource.Markov))));

        var first = WeightedSampler.Create<string>(entries: (ReadOnlySpan<(string Value, ulong Weight)>)[("red", 1UL), ("blue", 2UL)]).Sample(generator: ref rng);
        var second = WeightedSampler.Create<string>(entries: (ReadOnlySpan<(string Value, ulong Weight)>)[("fox", 1UL), ("hare", 1UL)]).Sample(generator: ref rng);

        return $"{first} {second}";
    }
}
