using Puck.Testing;
using Xunit;

namespace Puck.State.Generators.Tests;

/// <summary>CONTRACT UNDER TEST: a draw site's persisted state lives in the arena's runtime-state columns. Firing
/// reads the site's cursor and drawn masks out of them, advances the cursor by the samples the emission consumed,
/// and writes the masks the source persists back; two arenas over one seed draw one sequence; a site that reserves
/// no mask refuses a source that needs one; and a draw inside a journal scope rewinds with it.</summary>
public sealed class ArenaDrawLawTests {
    private const ulong Seed = 0x5EEDUL;

    private static long Fire(StateArena arena, ulong seed = Seed) {
        Assert.True(
            condition: ArenaDraws.TryFire(
                arena: arena,
                generator: TopologyArenaFixture.Bag(),
                reason: out var reason,
                result: out var result,
                rowOrdinal: TopologyArenaFixture.Deal,
                seed: GeneratorEngine.ComputeDrawSeed(documentSeed: seed, instanceIdentity: "law", site: "deal")
            ),
            userMessage: reason
        );

        return (result.Numeric ?? 0L);
    }

    [Fact]
    public void FiringAdvancesTheArenasCursorAndPersistsItsDrawnMask() {
        var (_, arena) = TopologyArenaFixture.Build();

        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: TopologyArenaFixture.Deal),
            expected: 0L
        );
        Assert.True(condition: arena.DrawnMask(
            index: 0,
            rowOrdinal: TopologyArenaFixture.Deal
        ).IsEmpty);

        var first = Fire(arena: arena);

        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: TopologyArenaFixture.Deal),
            expected: 1L
        );
        Assert.Equal(
            actual: arena.DrawnMask(
                index: 0,
                rowOrdinal: TopologyArenaFixture.Deal
            ).Count,
            expected: 1
        );

        var second = Fire(arena: arena);

        Assert.Equal(
            actual: arena.DrawnMask(
                index: 0,
                rowOrdinal: TopologyArenaFixture.Deal
            ).Count,
            expected: 2
        );
        Assert.NotEqual(
            actual: second,
            expected: first
        );

        var third = Fire(arena: arena);

        Assert.Equal(
            actual: new long[] { 1L, 2L, 3L },
            expected: new[] { first, second, third }.Order().ToArray()
        );

        // The pass is exhausted, and an exhausting source refuses the whole emission rather than redrawing.
        Assert.False(condition: ArenaDraws.TryFire(
            arena: arena,
            generator: TopologyArenaFixture.Bag(),
            reason: out var exhausted,
            result: out _,
            rowOrdinal: TopologyArenaFixture.Deal,
            seed: GeneratorEngine.ComputeDrawSeed(documentSeed: Seed, instanceIdentity: "law", site: "deal")
        ));
        Assert.NotEqual(
            actual: exhausted,
            expected: string.Empty
        );
    }
    [Fact]
    public void OneSeedDrawsOneSequenceAndAnotherSeedMovesIt() {
        var (_, first) = TopologyArenaFixture.Build();
        var (_, second) = TopologyArenaFixture.Build();
        var (_, other) = TopologyArenaFixture.Build();
        var left = new[] {
            Fire(arena: first),
            Fire(arena: first),
        };
        var right = new[] {
            Fire(arena: second),
            Fire(arena: second),
        };

        Assert.Equal(
            actual: right,
            expected: left
        );
        Assert.Equal(
            actual: second.ComputeHash(),
            expected: first.ComputeHash()
        );

        var moved = new[] {
            Fire(
                arena: other,
                seed: (Seed + 1UL)
            ),
            Fire(
                arena: other,
                seed: (Seed + 1UL)
            ),
        };

        Assert.NotEqual(
            actual: other.ComputeHash(),
            expected: first.ComputeHash()
        );
        Assert.NotEqual(
            actual: moved,
            expected: left
        );
    }
    [Fact]
    public void ADrawInsideAScopeRewindsWithIt() {
        var (_, arena) = TopologyArenaFixture.Build();
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        _ = Fire(arena: arena);

        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: before
        );
        arena.Rewind(mark: mark);
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: TopologyArenaFixture.Deal),
            expected: 0L
        );
    }
    [Fact]
    public void ASiteThatReservesNoMaskRefusesAnExhaustingSource() {
        var (_, arena) = TopologyArenaFixture.Build();

        Assert.Equal(
            actual: arena.Layout[TopologyArenaFixture.History].MaskCount,
            expected: 0
        );
        Assert.False(condition: ArenaDraws.TryFire(
            arena: arena,
            generator: TopologyArenaFixture.Bag(),
            reason: out var reason,
            result: out _,
            rowOrdinal: TopologyArenaFixture.History,
            seed: GeneratorEngine.ComputeDrawSeed(documentSeed: Seed, instanceIdentity: "law", site: "history")
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "drawn masks"
        );
        Assert.Null(@object: ArenaDraws.ReadMasks(
            arena: arena,
            rowOrdinal: TopologyArenaFixture.History
        ));
    }
}
