using Puck.Testing;
using Xunit;

namespace Puck.State.Generators.Tests;

/// <summary>CONTRACT UNDER TEST: a draw site's stream, sought once at the site's cursor, draws exactly the samples
/// single fires at the same site would draw one cursor apart, for a plain, an extended, an exhausting and a secret
/// source; closing it stores the cursor and masks those fires would have left; and a host's seed table, which folds the
/// instance identity once, seeds every site exactly as the four-rung ladder does.</summary>
public sealed class DrawStreamLawTests {
    private const string Instance = "an instance whose identity is long enough that folding it per draw would show";
    private const int Samples = 3;
    private const ulong Seed = 0x0DDBA11UL;

    private static DrawSeed SiteSeed() => GeneratorEngine.ComputeDrawSeed(
        documentSeed: Seed,
        instanceIdentity: Instance,
        site: "deal"
    );
    private static StateGenerator Plain() => new(Source: GeneratorSource.StreamDraw);
    private static StateGenerator Extended() => new(
        Source: GeneratorSource.StreamDraw,
        Extended: new GeneratorExtended(
            K: 8,
            Script: [7L, 11L]
        )
    );
    private static ClosedBitset256 Secret() => new(
        Word0: 0x1234_5678UL,
        Word1: 0UL,
        Word2: 0UL,
        Word3: 1UL
    );
    // The single-fire path, one arena write per sample: the reference the stream is held to.
    private static (long[] Values, long Hash) Fired(StateGenerator generator, ClosedBitset256? secret) {
        var (_, arena) = TopologyArenaFixture.Build();
        var values = new long[Samples];

        for (var index = 0; (index < values.Length); index++) {
            Assert.True(condition: ArenaDraws.TryFire(
                arena: arena,
                generator: generator,
                reason: out var reason,
                result: out var result,
                rowOrdinal: TopologyArenaFixture.Deal,
                secret: secret,
                seed: SiteSeed()
            ), userMessage: reason);
            values[index] = result.Numeric!.Value;
        }

        return (values, ((long)arena.ComputeHash()));
    }
    private static (long[] Values, long Hash) Streamed(StateGenerator generator, ClosedBitset256? secret) {
        var (_, arena) = TopologyArenaFixture.Build();
        var values = new long[Samples];

        Assert.True(condition: ArenaDraws.TryOpen(
            arena: arena,
            generator: generator,
            reason: out var reason,
            rowOrdinal: TopologyArenaFixture.Deal,
            secret: secret,
            seed: SiteSeed(),
            stream: out var stream
        ), userMessage: reason);
        for (var index = 0; (index < values.Length); index++) {
            Assert.True(condition: stream.TryNext(reason: out reason, value: out values[index]), userMessage: reason);
        }
        Assert.Equal(expected: Samples, actual: stream.Samples);
        Assert.True(condition: ArenaDraws.TryClose(arena: arena, reason: out reason, rowOrdinal: TopologyArenaFixture.Deal, stream: in stream), userMessage: reason);
        Assert.Equal(expected: ((long)Samples), actual: arena.DrawCursor(rowOrdinal: TopologyArenaFixture.Deal));

        return (values, ((long)arena.ComputeHash()));
    }
    private static void Agrees(StateGenerator generator, ClosedBitset256? secret = null) {
        var fired = Fired(generator: generator, secret: secret);
        var streamed = Streamed(generator: generator, secret: secret);

        Assert.Equal(actual: streamed.Values, expected: fired.Values);
        Assert.Equal(actual: streamed.Hash, expected: fired.Hash);
    }

    [Fact]
    public void APlainStreamDrawsWhatSingleFiresDraw() => Agrees(generator: Plain());
    [Fact]
    public void AnExtendedStreamDrawsWhatSingleFiresDraw() => Agrees(generator: Extended());
    [Fact]
    public void AnExhaustingStreamDrawsWhatSingleFiresDrawAndPersistsTheirMask() => Agrees(generator: TopologyArenaFixture.Bag());
    [Fact]
    public void ASecretStreamDrawsWhatSingleFiresDraw() => Agrees(generator: Plain(), secret: Secret());
    [Fact]
    public void AnExhaustedStreamRefusesTheSampleThatWouldOverdrawIt() {
        var (_, arena) = TopologyArenaFixture.Build();

        Assert.True(condition: ArenaDraws.TryOpen(arena: arena, generator: TopologyArenaFixture.Bag(), reason: out var reason, rowOrdinal: TopologyArenaFixture.Deal, seed: SiteSeed(), stream: out var stream), userMessage: reason);
        for (var index = 0; (index < 3); index++) {
            Assert.True(condition: stream.TryNext(reason: out reason, value: out _), userMessage: reason);
        }
        Assert.False(condition: stream.TryNext(reason: out reason, value: out _));
        Assert.NotEqual(actual: reason, expected: string.Empty);
        Assert.Equal(expected: 3L, actual: stream.Samples);
    }
    [Fact]
    public void ASeedTableSeedsEverySiteAsTheLadderDoes() {
        string[] sites = ["state.deal", "state.coin", string.Empty, new string(c: 'x', count: 4096)];
        var table = new ArenaDrawSeeds(
            documentSeed: Seed,
            instanceIdentity: Instance,
            sites: sites
        );

        Assert.Equal(expected: sites.Length, actual: table.Count);
        for (var ordinal = 0; (ordinal < sites.Length); ordinal++) {
            Assert.Equal(
                expected: GeneratorEngine.ComputeDrawSeed(documentSeed: Seed, instanceIdentity: Instance, site: sites[ordinal]),
                actual: table[ordinal]
            );
        }
    }
}
