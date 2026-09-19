using Xunit;

namespace Puck.State.Vectors.Tests;

/// <summary>CONTRACT UNDER TEST: <c>mix</c> and <c>mean</c> over an arena write exactly what the signed-byte
/// kernels compute from the same components, refuse by catalogued code, and leave the arena untouched when they
/// refuse.</summary>
public sealed class ArenaVectorBlendLawTests {
    private static sbyte[] KernelMean(StateArena arena, params string[] keys) {
        var catalog = arena.Catalog;
        var candidates = new ReadOnlyMemory<sbyte>[keys.Length];
        var destination = new sbyte[8];

        for (var index = 0; (index < keys.Length); index++) {
            candidates[index] = VectorArenaFixture.Read(
                arena: arena,
                key: VectorArenaFixture.Key(
                    catalog: catalog,
                    value: keys[index]
                ),
                rowOrdinal: VectorArenaFixture.Memories
            );
        }

        Assert.True(condition: VectorTransforms.TryMean(
            candidates: candidates,
            destination: destination,
            refusal: out _,
            sum: new long[destination.Length]
        ));

        return destination;
    }
    private static sbyte[] KernelMix(StateArena arena, params (string Key, int Weight)[] terms) {
        var catalog = arena.Catalog;
        var destination = new sbyte[8];
        var vectors = new ReadOnlyMemory<sbyte>[terms.Length];
        var weights = new int[terms.Length];

        for (var index = 0; (index < terms.Length); index++) {
            vectors[index] = VectorArenaFixture.Read(
                arena: arena,
                key: VectorArenaFixture.Key(
                    catalog: catalog,
                    value: terms[index].Key
                ),
                rowOrdinal: VectorArenaFixture.Memories
            );
            weights[index] = terms[index].Weight;
        }

        Assert.True(condition: VectorTransforms.TryMix(
            destination: destination,
            refusal: out _,
            sum: new long[destination.Length],
            vectors: vectors,
            weights: weights
        ));

        return destination;
    }
    private static VectorMixTerm Term(StateCatalog catalog, string key, int weight) => new(
        Source: VectorSource.Cell(
            key: VectorArenaFixture.Key(
                catalog: catalog,
                value: key
            ),
            rowOrdinal: VectorArenaFixture.Memories
        ),
        Weight: weight
    );

    // The transform's arithmetic is the kernel's, so the written bytes are the kernel's answer over the same
    // stored components and nothing the transform computed on the side.
    [Fact]
    public void AMixWritesTheKernelsWeightedSum() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var expected = KernelMix(
            arena: arena,
            terms: [("north", 3), ("east", 1)]
        );
        var slot = VectorArenaFixture.SlotKey(catalog: catalog);

        Assert.True(
            condition: ArenaVectorTransforms.TryMix(
                arena: arena,
                intoKey: slot,
                intoRowOrdinal: VectorArenaFixture.Stance,
                refusal: out var refusal,
                terms: [
                    Term(
                        catalog: catalog,
                        key: "north",
                        weight: 3
                    ),
                    Term(
                        catalog: catalog,
                        key: "east",
                        weight: 1
                    ),
                ]
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: expected
        );
    }
    [Fact]
    public void AMixSummingToZeroRefusesAndWritesNothing() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var slot = VectorArenaFixture.SlotKey(catalog: catalog);
        var before = VectorArenaFixture.Read(
            arena: arena,
            key: slot,
            rowOrdinal: VectorArenaFixture.Stance
        );

        Assert.False(condition: ArenaVectorTransforms.TryMix(
            arena: arena,
            intoKey: slot,
            intoRowOrdinal: VectorArenaFixture.Stance,
            refusal: out var refusal,
            terms: [
                Term(
                    catalog: catalog,
                    key: "north",
                    weight: 4
                ),
                Term(
                    catalog: catalog,
                    key: "north",
                    weight: -4
                ),
            ]
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorMixZero
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: before
        );
    }
    // The arena lays a vector row out by its dimension count, so a term from a wider space is refused by that
    // count rather than silently truncated.
    [Fact]
    public void AMixTermFromAWiderSpaceRefuses() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.False(condition: ArenaVectorTransforms.TryMix(
            arena: arena,
            intoKey: VectorArenaFixture.SlotKey(catalog: catalog),
            intoRowOrdinal: VectorArenaFixture.Stance,
            refusal: out var refusal,
            terms: [new VectorMixTerm(
                    Source: VectorSource.Cell(
                        key: VectorArenaFixture.Key(
                            catalog: catalog,
                            value: "far"
                        ),
                        rowOrdinal: VectorArenaFixture.Wide
                    ),
                    Weight: 1
                )]
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorSpaceMismatch
        );
    }
    [InlineData(0)]
    [InlineData(StateCapacity.MaxMixTerms + 1)]
    [Theory]
    public void AMixOutsideTheTermCeilingRefuses(int count) {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var terms = new VectorMixTerm[count];

        for (var index = 0; (index < count); index++) {
            terms[index] = Term(
                catalog: catalog,
                key: "north",
                weight: 1
            );
        }

        Assert.False(condition: ArenaVectorTransforms.TryMix(
            arena: arena,
            intoKey: VectorArenaFixture.SlotKey(catalog: catalog),
            intoRowOrdinal: VectorArenaFixture.Stance,
            refusal: out var refusal,
            terms: terms
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorMixTerms
        );
    }
    [Fact]
    public void AMixIntoARowThatStoresNoVectorRefuses() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.False(condition: ArenaVectorTransforms.TryMix(
            arena: arena,
            intoKey: VectorArenaFixture.SlotKey(catalog: catalog),
            intoRowOrdinal: VectorArenaFixture.Count,
            refusal: out var refusal,
            terms: [Term(
                    catalog: catalog,
                    key: "north",
                    weight: 1
                )]
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorOperandNotVector
        );
    }
    // The filter decides membership of the average, so an unfiltered mean over the same table is a different
    // direction: a law that only checked the filtered answer would pass on a filter that was never read.
    [Fact]
    public void AMeanAveragesOnlyWhatItsFilterAdmits() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var filtered = KernelMean(
            arena: arena,
            keys: ["north", "up"]
        );
        var slot = VectorArenaFixture.SlotKey(catalog: catalog);
        var whole = KernelMean(
            arena: arena,
            keys: ["north", "east", "up"]
        );

        Assert.True(
            condition: ArenaVectorTransforms.TryMean(
                arena: arena,
                refusal: out var refusal,
                request: new VectorMeanRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoKey: slot,
                    IntoRowOrdinal: VectorArenaFixture.Stance,
                    WhereRowOrdinal: VectorArenaFixture.Active
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: filtered
        );
        Assert.NotEqual(
            actual: whole,
            expected: filtered
        );
        Assert.True(
            condition: ArenaVectorTransforms.TryMean(
                arena: arena,
                refusal: out refusal,
                request: new VectorMeanRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoKey: slot,
                    IntoRowOrdinal: VectorArenaFixture.Stance
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: whole
        );
    }
    [Fact]
    public void AMeanWithNoAdmittedCellRefuses() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        foreach (var key in new[] { "north", "east", "up" }) {
            Assert.True(condition: arena.TryWrite(
                key: VectorArenaFixture.Key(
                    catalog: catalog,
                    value: key
                ),
                operand: 0L,
                reason: out var reason,
                rowOrdinal: VectorArenaFixture.Active,
                write: StateWriteKind.Set
            ), userMessage: reason);
        }

        Assert.False(condition: ArenaVectorTransforms.TryMean(
            arena: arena,
            refusal: out var refusal,
            request: new VectorMeanRequest(
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoKey: VectorArenaFixture.SlotKey(catalog: catalog),
                IntoRowOrdinal: VectorArenaFixture.Stance,
                WhereRowOrdinal: VectorArenaFixture.Active
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorMeanEmpty
        );
    }
    [Fact]
    public void AMeanWhoseFilterIsNotAKeyedBoolRowRefuses() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);

        Assert.False(condition: ArenaVectorTransforms.TryMean(
            arena: arena,
            refusal: out var refusal,
            request: new VectorMeanRequest(
                FromRowOrdinal: VectorArenaFixture.Memories,
                IntoKey: VectorArenaFixture.SlotKey(catalog: catalog),
                IntoRowOrdinal: VectorArenaFixture.Stance,
                WhereRowOrdinal: VectorArenaFixture.Count
            )
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RuleRefusal.VectorFilterShape
        );
    }
    // A transform's writes are ordinary journalled writes, so the caller's own scope rewinds them with everything
    // else it recorded.
    [Fact]
    public void ATransformsWriteRewindsWithTheCallersScope() {
        var arena = VectorArenaFixture.Arena(catalog: out var catalog);
        var slot = VectorArenaFixture.SlotKey(catalog: catalog);
        var before = VectorArenaFixture.Read(
            arena: arena,
            key: slot,
            rowOrdinal: VectorArenaFixture.Stance
        );
        var mark = arena.BeginScope();

        Assert.True(
            condition: ArenaVectorTransforms.TryMean(
                arena: arena,
                refusal: out var refusal,
                request: new VectorMeanRequest(
                    FromRowOrdinal: VectorArenaFixture.Memories,
                    IntoKey: slot,
                    IntoRowOrdinal: VectorArenaFixture.Stance
                )
            ),
            userMessage: refusal.ToString()
        );
        Assert.NotEqual(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: before
        );

        arena.Rewind(mark: mark);

        Assert.Equal(
            actual: VectorArenaFixture.Read(
                arena: arena,
                key: slot,
                rowOrdinal: VectorArenaFixture.Stance
            ),
            expected: before
        );
    }
}
