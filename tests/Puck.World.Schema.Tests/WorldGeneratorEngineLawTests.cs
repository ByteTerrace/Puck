using Puck.Maths;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for authored randomness' source x site x moment split. The source is a stateless declaration, the site
/// descriptor owns stream identity and cursor/masks, and timing controls when a site fires without becoming another
/// seed input.
/// </summary>
public sealed class WorldGeneratorEngineLawTests {
    private const string Instance = "instance-alpha";
    private const ulong WorldSeed = 0x0123_4567_89AB_CDEFUL;

    private static readonly StateGenerator s_stream = new(Source: GeneratorSource.StreamDraw);
    private static readonly StateGenerator s_uniform = new(
        Source: GeneratorSource.UniformRange,
        RangeMin: -17,
        RangeMax: 29
    );
    private static readonly StateGenerator s_weighted = new(
        Source: GeneratorSource.WeightedNumeric,
        Weighted: [
            new GeneratorWeightedNumeric(Value: -11, Weight: 1UL),
            new GeneratorWeightedNumeric(Value: 7, Weight: 3UL),
            new GeneratorWeightedNumeric(Value: 101, Weight: 5UL),
        ]
    );
    private static readonly StateGenerator s_twoTokenMarkov = new(
        Source: GeneratorSource.Markov,
        Start: Name(value: "start"),
        Bound: 2,
        Contexts: [
            new GeneratorContext(
                Key: Name(value: "start"),
                Alternatives: [
                    new GeneratorAlternative(Token: "red", Weight: 1UL, Next: Name(value: "tail")),
                    new GeneratorAlternative(Token: "blue", Weight: 2UL, Next: Name(value: "tail")),
                ]
            ),
            new GeneratorContext(
                Key: Name(value: "tail"),
                Alternatives: [
                    new GeneratorAlternative(Token: "fox", Weight: 1UL, Next: Name(value: "done")),
                    new GeneratorAlternative(Token: "hare", Weight: 1UL, Next: Name(value: "done")),
                ]
            ),
            new GeneratorContext(Key: Name(value: "done")),
        ]
    );

    [Fact]
    public void SourcesDeclareTheFixedAdvanceCostCursorSeekingDependsOn() {
        Assert.Equal(expected: 2UL, actual: GeneratorEngine.AdvancesPerSample(source: GeneratorSource.Markov));
        Assert.Equal(expected: 1UL, actual: GeneratorEngine.AdvancesPerSample(source: GeneratorSource.UniformRange));
        Assert.Equal(expected: 2UL, actual: GeneratorEngine.AdvancesPerSample(source: GeneratorSource.WeightedNumeric));
        Assert.Equal(expected: 1UL, actual: GeneratorEngine.AdvancesPerSample(source: GeneratorSource.StreamDraw));
    }
    [Fact]
    public void NumericSourcesSeekToTheSameSampleAsWalkingEveryPriorCursor() {
        foreach (var generator in ((StateGenerator[])[s_stream, s_uniform, s_weighted])) {
            for (var cursor = 0L; (cursor < 32L); cursor++) {
                var fired = Fire(generator: generator, site: "state.loot", cursor: cursor);

                Assert.Equal(expected: 1L, actual: fired.Samples);
                Assert.Equal(
                    expected: NumericOracle(cursor: cursor, generator: generator, site: "state.loot"),
                    actual: fired.Numeric
                );
            }
        }
    }
    [Fact]
    public void MarkovCursorCountsSamplesRatherThanEmissions() {
        var cursor = 0L;
        var walked = new List<(long Cursor, string Text)>();

        for (var emission = 0; (emission < 16); emission++) {
            var fired = Fire(generator: s_twoTokenMarkov, site: "state.name", cursor: cursor);

            Assert.Equal(expected: 2L, actual: fired.Samples);
            Assert.Equal(expected: TwoTokenMarkovOracle(cursor: cursor, site: "state.name"), actual: fired.Text);
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
        var sourceName = Name(value: "shared");
        var sources = ((IReadOnlyList<GeneratorRow>)[new GeneratorRow(Generator: s_weighted, Name: sourceName)]);

        foreach (var timing in Enum.GetValues<DrawTiming>()) {
            var draw = new Draw(Source: sourceName, Timing: timing);

            Assert.True(condition: GeneratorEngine.TryResolveSource(
                draw: draw,
                generator: out var resolved,
                generators: sources,
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

        Assert.Equal(actual: afterOtherSiteAdvanced, expected: baseline);
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
    public void PersistedCursorAndMaskReplayTheNextWithoutReplacementDraw() {
        var generator = DrawGenerator(mode: GeneratorMode.WithoutReplacement);
        var site = "state.token";
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;
        var drawn = new List<string>();
        var snapshots = new List<(long Cursor, IReadOnlyList<ClosedBitset256>? Masks)>();

        for (var draw = 0; (draw < 3); draw++) {
            snapshots.Add(item: (cursor, masks?.ToArray()));
            var fired = Fire(generator: generator, site: site, cursor: cursor, masks: masks);

            drawn.Add(item: fired.Text!);
            cursor += fired.Samples;
            masks = fired.Masks;
        }

        Assert.Equal(expected: 3, actual: drawn.Distinct(comparer: StringComparer.Ordinal).Count());

        for (var draw = 0; (draw < drawn.Count); draw++) {
            var snapshot = snapshots[draw];
            var replayed = Fire(generator: generator, site: site, cursor: snapshot.Cursor, masks: snapshot.Masks);

            Assert.Equal(expected: drawn[draw], actual: replayed.Text);
        }

        Assert.False(condition: TryFire(
            generator: generator,
            site: site,
            cursor: cursor,
            masks: masks,
            result: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "drawn out");
    }
    [Fact]
    public void RestartOnExhaustionStartsANewDeterministicPass() {
        var generator = DrawGenerator(mode: GeneratorMode.RestartOnExhaustion);
        var site = "state.token";
        var cursor = 0L;
        IReadOnlyList<ClosedBitset256>? masks = null;

        for (var draw = 0; (draw < 3); draw++) {
            var fired = Fire(generator: generator, site: site, cursor: cursor, masks: masks);

            cursor += fired.Samples;
            masks = fired.Masks;
        }

        var restarted = Fire(generator: generator, site: site, cursor: cursor, masks: masks);
        var replayed = Fire(generator: generator, site: site, cursor: cursor, masks: masks?.ToArray());

        Assert.Equal(expected: restarted.Text, actual: replayed.Text);
        Assert.Equal(expected: restarted.Numeric, actual: replayed.Numeric);
        Assert.Equal(expected: restarted.Samples, actual: replayed.Samples);
        Assert.Equal(expected: restarted.Masks, actual: replayed.Masks);
        Assert.NotNull(@object: restarted.Masks);
        Assert.Equal(expected: 1, actual: restarted.Masks![0].Count);
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateGenerator DrawGenerator(GeneratorMode mode) => new(
        Source: GeneratorSource.Markov,
        Start: Name(value: "pool"),
        Contexts: [
            new GeneratorContext(
                Key: Name(value: "pool"),
                Alternatives: [
                    new GeneratorAlternative(Token: "one", Weight: 1UL, Next: Name(value: "done")),
                    new GeneratorAlternative(Token: "two", Weight: 1UL, Next: Name(value: "done")),
                    new GeneratorAlternative(Token: "three", Weight: 1UL, Next: Name(value: "done")),
                ]
            ),
            new GeneratorContext(Key: Name(value: "done")),
        ],
        Mode: mode
    );
    private static GeneratorEngine.FireResult Fire(
        StateGenerator generator,
        string site,
        long cursor,
        IReadOnlyList<ClosedBitset256>? masks = null,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) {
        Assert.True(condition: TryFire(
            cursor: cursor,
            masks: masks,
            generator: generator,
            instance: instance,
            reason: out var reason,
            result: out var result,
            site: site,
            worldSeed: worldSeed
        ), userMessage: reason);

        return result;
    }
    private static bool TryFire(
        StateGenerator generator,
        string site,
        long cursor,
        IReadOnlyList<ClosedBitset256>? masks,
        out GeneratorEngine.FireResult result,
        out string reason,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) => GeneratorEngine.TryFire(
        generator: generator,
        targetKind: ((generator.Source == GeneratorSource.Markov) ? CellKind.Text : CellKind.Int),
        seedState: GeneratorEngine.ComputeSeedState(instanceIdentity: instance, site: site, documentSeed: worldSeed),
        stream: GeneratorEngine.ComputeStreamId(site: site),
        cursor: cursor,
        masks: masks,
        result: out result,
        reason: out reason
    );
    private static long[] NumericSequence(
        StateGenerator generator,
        string site,
        int count,
        ulong worldSeed = WorldSeed,
        string instance = Instance
    ) {
        var values = new long[count];

        for (var cursor = 0L; (cursor < count); cursor++) {
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
    private static long NumericOracle(StateGenerator generator, string site, long cursor) {
        var rng = Pcg32XshRr.Create(
            state: GeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: site, documentSeed: WorldSeed),
            stream: GeneratorEngine.ComputeStreamId(site: site)
        );

        rng.Advance(count: unchecked((((ulong)cursor) * GeneratorEngine.AdvancesPerSample(source: generator.Source))));

        return generator.Source switch {
            GeneratorSource.StreamDraw => rng.NextUInt32(),
            GeneratorSource.UniformRange => (generator.RangeMin!.Value + ((long)(((((ulong)((uint)(generator.RangeMax!.Value - generator.RangeMin.Value))) + 1UL) * rng.NextUnitFraction32().Value) >> 32))),
            GeneratorSource.WeightedNumeric => WeightedSampler.Create<long>(entries: generator.Weighted!.Select(selector: static row => (row.Value, row.Weight)).ToArray()).Sample(generator: ref rng),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(generator)),
        };
    }
    private static string TwoTokenMarkovOracle(string site, long cursor) {
        var rng = Pcg32XshRr.Create(
            state: GeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: site, documentSeed: WorldSeed),
            stream: GeneratorEngine.ComputeStreamId(site: site)
        );

        rng.Advance(count: unchecked((((ulong)cursor) * GeneratorEngine.AdvancesPerSample(source: GeneratorSource.Markov))));

        var first = WeightedSampler.Create<string>(entries: ((ReadOnlySpan<(string Value, ulong Weight)>)[("red", 1UL), ("blue", 2UL)])).Sample(generator: ref rng);
        var second = WeightedSampler.Create<string>(entries: ((ReadOnlySpan<(string Value, ulong Weight)>)[("fox", 1UL), ("hare", 1UL)])).Sample(generator: ref rng);

        return $"{first} {second}";
    }
}
