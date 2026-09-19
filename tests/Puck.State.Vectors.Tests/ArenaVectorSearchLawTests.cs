using Puck.Maths;
using Xunit;

namespace Puck.State.Vectors.Tests;

/// <summary>CONTRACT UNDER TEST: <c>nearest</c> ranks a keyed vector table into the destination its kind and shape
/// admit and replaces whatever that destination held, <c>remember</c> stores a vector unless another cell of its
/// table is within the cosine threshold, and a refusal after the first write leaves the arena as it was.</summary>
public sealed class ArenaVectorSearchLawTests {
    private static readonly long NearThreshold = ((FixedQ4816.One.Value * 9L) / 10L);

    private static VectorSource Query(StateCatalog catalog, string key) => VectorSource.Cell(
        key: VectorArenaFixture.Key(
            catalog: catalog,
            value: key
        ),
        rowOrdinal: VectorArenaFixture.Memories
    );
    private static string[] RankedKeys(StateArena arena, int rowOrdinal) {
        var keys = new string[arena.CellCount(rowOrdinal: rowOrdinal)];

        for (var position = 0; (position < keys.Length); position++) {
            Assert.True(condition: arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            ));

            keys[position] = arena.Catalog.Keys[key: key].Value;
        }

        return keys;
    }

    // The candidates and the ranking are leased from the arena's scratch. The rows a ranking writes are the
    // arena's own to grow, so the law holds the transform to the same request repeated, where they no longer do.
    [Fact]
    public void ARepeatedNearestAllocatesNothing() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var request = new VectorNearestRequest(
            FromRowOrdinal: VectorArenaFixture.Memories,
            IntoRowOrdinal: VectorArenaFixture.Recalled,
            K: 2,
            Query: Query(
                catalog: catalog,
                key: "north"
            )
        );

        for (var warm = 0; (warm < 4); warm++) {
            Assert.True(condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out _,
                request: request
            ));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var round = 0; (round < 256); round++) {
            _ = ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out _,
                request: request
            );
        }

        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
    }
    // An Int destination scores by exact dot product, so the ranking is the kernel's order and the stored value is
    // the kernel's score rather than a rank index.
    [Fact]
    public void ANearestIntoAKeyedIntTableStoresKeysRankedByDotProduct() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out var refusal,
                request: new VectorNearestRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoRowOrdinal: VectorArenaFixture.Recalled,
                    K: 2,
                    Query: Query(
                        catalog: catalog,
                        key: "north"
                    )
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Recalled
            ),
            expected: ["north", "up"]
        );
        Assert.True(condition: arena.TryRead(
            key: VectorArenaFixture.Key(
                catalog: catalog,
                value: "north"
            ),
            rowOrdinal: VectorArenaFixture.Recalled,
            value: out var winner
        ));
        Assert.Equal(
            actual: winner.AsInt,
            expected: SignedByteVectorFunctions.Dot(
                left: VectorArenaFixture.Unit(axis: 0).Components,
                right: VectorArenaFixture.Unit(axis: 0).Components
            )
        );
    }
    // A Fixed destination scores by Q48.16 cosine, which is the distinction the two kinds carry.
    [Fact]
    public void ANearestIntoAKeyedFixedTableStoresCosineScores() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out var refusal,
                request: new VectorNearestRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoRowOrdinal: VectorArenaFixture.Ranked,
                    K: 1,
                    Query: Query(
                        catalog: catalog,
                        key: "north"
                    )
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.True(condition: arena.TryRead(
            key: VectorArenaFixture.Key(
                catalog: catalog,
                value: "north"
            ),
            rowOrdinal: VectorArenaFixture.Ranked,
            value: out var winner
        ));
        Assert.Equal(
            actual: winner.AsFixed,
            expected: FixedQ4816.One.Value
        );
    }
    [Fact]
    public void ANearestIntoATextSlotNamesTheWinner() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out var refusal,
                request: new VectorNearestRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoRowOrdinal: VectorArenaFixture.Reply,
                    K: 1,
                    Query: Query(
                        catalog: catalog,
                        key: "east"
                    )
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.True(condition: arena.TryRead(
            key: VectorArenaFixture.SlotKey(catalog: catalog),
            rowOrdinal: VectorArenaFixture.Reply,
            value: out var named
        ));
        Assert.Equal(
            actual: named.AsText,
            expected: "east"
        );
    }
    [Fact]
    public void ANearestSkipsTheKeyItExcludes() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out var refusal,
                request: new VectorNearestRequest(
                    Exclude: VectorArenaFixture.Key(
                        catalog: catalog,
                        value: "north"
                    ),
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoRowOrdinal: VectorArenaFixture.Recalled,
                    K: 1,
                    Query: Query(
                        catalog: catalog,
                        key: "north"
                    )
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Recalled
            ),
            expected: ["up"]
        );
    }
    [Fact]
    public void ANearestRanksOnlyWhatItsFilterAdmits() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryNearest(
                arena: arena,
                refusal: out var refusal,
                request: new VectorNearestRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoRowOrdinal: VectorArenaFixture.Recalled,
                    K: 3,
                    Query: Query(
                        catalog: catalog,
                        key: "north"
                    ),
                    WhereRowOrdinal: VectorArenaFixture.Active
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Recalled
            ),
            expected: ["north", "up"]
        );
    }
    // The destination names exactly the matches this ranking found, so a key an earlier ranking left is gone
    // rather than outranking a fresh one by having been there first.
    [Fact]
    public void ANearestReplacesWhateverTheDestinationHeld() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(condition: ArenaVectorTransforms.TryNearest(
            arena: arena,
            refusal: out _,
            request: new VectorNearestRequest(
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoRowOrdinal: VectorArenaFixture.Recalled,
                K: 3,
                Query: Query(
                    catalog: catalog,
                    key: "north"
                )
            )
        ));
        Assert.True(condition: ArenaVectorTransforms.TryNearest(
            arena: arena,
            refusal: out _,
            request: new VectorNearestRequest(
                Exclude: VectorArenaFixture.Key(
                    catalog: catalog,
                    value: "north"
                ),
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoRowOrdinal: VectorArenaFixture.Recalled,
                K: 1,
                Query: Query(
                    catalog: catalog,
                    key: "east"
                )
            )
        ));
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Recalled
            ),
            expected: ["east"]
        );
    }
    [InlineData(VectorArenaFixture.Stance, 1)]
    [InlineData(VectorArenaFixture.Recalled, 0)]
    [InlineData(VectorArenaFixture.Recalled, 5)]
    [InlineData(VectorArenaFixture.Reply, 2)]
    [Theory]
    public void ANearestTheDestinationCannotTakeRefuses(int intoRowOrdinal, int k) {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.False(condition: ArenaVectorTransforms.TryNearest(
            arena: arena,
            refusal: out var refusal,
            request: new VectorNearestRequest(
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoRowOrdinal: intoRowOrdinal,
                K: k,
                Query: Query(
                    catalog: catalog,
                    key: "north"
                )
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorNearestShape
        );
    }
    // A score decides through the destination row's own admission, and the scope the transform opened carries the
    // cleared cells back when one refuses.
    [Fact]
    public void ANearestScoreTheDestinationCannotHoldRewindsTheWholeRanking() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.True(condition: arena.TryMint(
            key: out _,
            name: VectorArenaFixture.Name(value: "old"),
            reason: out var mintReason,
            rowOrdinal: VectorArenaFixture.Capped,
            value: CellValue.Int(value: 5L)
        ), userMessage: mintReason);
        Assert.False(condition: ArenaVectorTransforms.TryNearest(
            arena: arena,
            refusal: out var refusal,
            request: new VectorNearestRequest(
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoRowOrdinal: VectorArenaFixture.Capped,
                K: 1,
                Query: Query(
                    catalog: catalog,
                    key: "north"
                )
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleEffectRefusal.MutationRejected
        );
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Capped
            ),
            expected: ["old"]
        );
        Assert.True(condition: arena.TryRead(
            key: VectorArenaFixture.Key(
                catalog: catalog,
                value: "old"
            ),
            rowOrdinal: VectorArenaFixture.Capped,
            value: out var survivor
        ));
        Assert.Equal(
            actual: survivor.AsInt,
            expected: 5L
        );
    }
    [Fact]
    public void ARememberStoresAVectorItsTableDoesNotHold() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var stored = VectorArenaFixture.Unit(axis: 4);

        Assert.True(
            condition: ArenaVectorTransforms.TryRemember(
                arena: arena,
                refusal: out var refusal,
                request: new VectorRememberRequest(
                    From: VectorSource.Literal(components: stored.Memory),
                    IntoRowOrdinal: VectorArenaFixture.Journal,
                    Key: VectorArenaFixture.Name(value: "second"),
                    UnlessWithinQ16: NearThreshold
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: VectorArenaFixture.Key(
                    catalog: catalog,
                    value: "second"
                ),
                rowOrdinal: VectorArenaFixture.Journal
            ),
            expected: stored.Components.ToArray()
        );
    }
    // A near duplicate is a success that writes nothing, because the table already remembers the direction.
    [Fact]
    public void ARememberSkipsANearDuplicateWithoutWriting() {
        var arena = VectorArenaFixture.Arena(catalog: out _);
        var near = VectorArenaFixture.Blend(
            first: 0,
            firstWeight: 40L,
            second: 1,
            secondWeight: 1L
        );

        Assert.True(
            condition: ArenaVectorTransforms.TryRemember(
                arena: arena,
                refusal: out var refusal,
                request: new VectorRememberRequest(
                    From: VectorSource.Literal(components: near.Memory),
                    IntoRowOrdinal: VectorArenaFixture.Journal,
                    Key: VectorArenaFixture.Name(value: "second"),
                    UnlessWithinQ16: NearThreshold
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: RankedKeys(
                arena: arena,
                rowOrdinal: VectorArenaFixture.Journal
            ),
            expected: ["first"]
        );
    }
    // The cell a remember targets is never its own near duplicate, so a refreshed vector lands under the key it
    // already occupies instead of being skipped by itself.
    [Fact]
    public void ARememberIgnoresItsOwnKeyWhenLookingForADuplicate() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var first = VectorArenaFixture.Key(
            catalog: catalog,
            value: "first"
        );
        var near = VectorArenaFixture.Blend(
            first: 0,
            firstWeight: 40L,
            second: 1,
            secondWeight: 1L
        );

        Assert.NotEqual(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: first,
                rowOrdinal: VectorArenaFixture.Journal
            ),
            expected: near.Components.ToArray()
        );
        Assert.True(
            condition: ArenaVectorTransforms.TryRemember(
                arena: arena,
                refusal: out var refusal,
                request: new VectorRememberRequest(
                    From: VectorSource.Literal(components: near.Memory),
                    IntoRowOrdinal: VectorArenaFixture.Journal,
                    Key: VectorArenaFixture.Name(value: "first"),
                    UnlessWithinQ16: NearThreshold
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: first,
                rowOrdinal: VectorArenaFixture.Journal
            ),
            expected: near.Components.ToArray()
        );
    }
    [InlineData(-1L)]
    [InlineData(70000L)]
    [Theory]
    public void ARememberThresholdOutsideTheUnitIntervalRefuses(long unlessWithin) {
        var arena = VectorArenaFixture.Arena(catalog: out _);

        Assert.False(condition: ArenaVectorTransforms.TryRemember(
            arena: arena,
            refusal: out var refusal,
            request: new VectorRememberRequest(
                From: VectorSource.Literal(components: VectorArenaFixture.Unit(axis: 4).Memory),
                IntoRowOrdinal: VectorArenaFixture.Journal,
                Key: VectorArenaFixture.Name(value: "second"),
                UnlessWithinQ16: unlessWithin
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorRememberShape
        );
    }
    [Fact]
    public void ARememberOfAVectorFromAnotherSpaceRefuses() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.False(condition: ArenaVectorTransforms.TryRemember(
            arena: arena,
            refusal: out var refusal,
            request: new VectorRememberRequest(
                From: VectorSource.Cell(
                    key: VectorArenaFixture.Key(
                        catalog: catalog,
                        value: "far"
                    ),
                    rowOrdinal: VectorArenaFixture.Wide
                ),
                IntoRowOrdinal: VectorArenaFixture.Journal,
                Key: VectorArenaFixture.Name(value: "second"),
                UnlessWithinQ16: NearThreshold
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorSpaceMismatch
        );
    }
}
