using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the arena hash folds every stored column of every lane, so mutating any single
/// cell or runtime-state field moves it; every variable-length part folds its own length, so two records that
/// differ only in where their parts divide fold differently; a host-owned row is outside the fold; and two arenas
/// over the same document and writes fold to the same value.</summary>
public sealed class ArenaHashLawTests {
    public static TheoryData<string> Mutations => [
        "number",
        "text",
        "vector",
        "clockEpochTick",
        "clockEpochEngineTick",
        "clockY0",
        "clockV0",
        "clockSubstepTicks",
        "behavior",
        "provenance",
        "visibility",
        "observation",
        "presence",
        "member",
        "membership",
        "ring",
        "board",
        "drawCursor",
        "drawnMask",
        "phaseSequence",
        "laneRoster",
        "laneNumber",
    ];

    [MemberData(memberName: nameof(Mutations))]
    [Theory]
    public void MutatingAnySingleColumnMovesTheHash(string mutation) {
        var (catalog, arena) = ArenaFixture.Build();
        var before = arena.ComputeHash();

        Mutate(
            arena: arena,
            catalog: catalog,
            mutation: mutation
        );

        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void TwoArenasOverOneDocumentAndTheSameWritesFoldTheSameValue() {
        var (firstCatalog, first) = ArenaFixture.Build();
        var (secondCatalog, second) = ArenaFixture.Build();

        Assert.Equal(
            actual: second.ComputeHash(),
            expected: first.ComputeHash()
        );

        ArenaSerializationLawTests.Populate(
            arena: first,
            catalog: firstCatalog
        );
        ArenaSerializationLawTests.Populate(
            arena: second,
            catalog: secondCatalog
        );

        Assert.Equal(
            actual: second.ComputeHash(),
            expected: first.ComputeHash()
        );
    }
    [Fact]
    public void AHostOwnedRowIsOutsideTheFold() {
        var (_, arena) = ArenaFixture.Build();
        var before = arena.ComputeHash();

        Assert.False(condition: arena.Layout[ArenaFixture.Field].IsStored);
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Field),
            expected: 0
        );
        Assert.False(condition: arena.TryWriteBoardCell(
            cell: 0,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Field,
            value: 7L,
            write: StateWriteKind.Set
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "field"
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [InlineData(null, new[] { "a" }, "a", new string[0])]
    [InlineData("a", new[] { "b" }, "ab", new string[0])]
    [InlineData(null, new[] { "a", "b" }, null, new[] { "ab" })]
    [InlineData(null, new[] { "ab" }, null, new[] { "a", "b" })]
    [Theory]
    public void TwoVisibilityRecordsThatDifferOnlyInTheirDivisionsFoldDifferently(string? firstFrom, string[] firstReaders, string? secondFrom, string[] secondReaders) {
        Assert.NotEqual(
            actual: VisibilityHash(
                readers: secondReaders,
                readersFrom: secondFrom
            ),
            expected: VisibilityHash(
                readers: firstReaders,
                readersFrom: firstFrom
            )
        );
    }
    [Fact]
    public void ASectionAuthoringCellsOnAHostOwnedRowRefusesByName() {
        var section = ArenaFixture.Section();
        var rows = new List<StateRow>(collection: section.Rows!);

        Assert.True(condition: rows[ArenaFixture.Field].HostOwned);

        rows[ArenaFixture.Field] = (rows[ArenaFixture.Field] with {
            Cells = [new StateCell(
                Key: ArenaFixture.Name(value: "0"),
                Value: CellValue.Fixed(rawBits: 7L)
            )],
        });

        var served = (section with { Rows = rows });
        var refusal = Assert.Throws<ArgumentException>(testCode: () => new StateArena(
            catalog: StateCatalog.Compile(section: served),
            options: null,
            section: served,
            time: ArenaTime.Origin
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "field"
        );
    }
    [Fact]
    public void ARewoundScopeFoldsBackToWhatItOpenedOn() {
        var (catalog, arena) = ArenaFixture.Build();
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        ArenaSerializationLawTests.Populate(
            arena: arena,
            catalog: catalog
        );
        arena.Rewind(mark: mark);

        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }

    private static ulong VisibilityHash(string? readersFrom, string[] readers) {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(condition: arena.TryWriteVisibility(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "a"
            ),
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(
                Readers: readers,
                ReadersFrom: readersFrom
            )
        ));

        return arena.ComputeColumnHash(column: ArenaColumn.Visibility);
    }
    private static void Mutate(StateArena arena, StateCatalog catalog, string mutation) {
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        switch (mutation) {
            case "number":
                Assert.True(condition: arena.TryWrite(
                    key: slot,
                    operand: 9L,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Score,
                    write: StateWriteKind.Set
                ));

                break;
            case "text":
                Assert.True(condition: arena.TryWriteText(
                    key: slot,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Label,
                    text: "bye"
                ));

                break;
            case "vector":
                Assert.True(condition: arena.TryWriteVector(
                    components: ArenaFixture.Unit(axis: 3).Components,
                    key: slot,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Embed
                ));

                break;
            case "clockEpochTick":
            case "clockEpochEngineTick":
            case "clockY0":
            case "clockV0":
            case "clockSubstepTicks":
                Assert.True(condition: arena.TryWriteClock(
                    epochEngineTick: ((mutation == "clockEpochEngineTick") ? 4L : 0L),
                    epochTick: ((mutation == "clockEpochTick") ? 3L : 0L),
                    key: a,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Tokens,
                    substepTicks: ((mutation == "clockSubstepTicks") ? 7L : 0L),
                    v0: ((mutation == "clockV0") ? 6L : 0L),
                    y0: ((mutation == "clockY0") ? 5L : 0L)
                ));

                break;
            case "behavior":
                Assert.True(condition: arena.TryWriteBehavior(
                    behavior: StateCellBehavior.None,
                    key: a,
                    rowOrdinal: ArenaFixture.Tokens
                ));

                break;
            case "provenance":
                Assert.True(condition: arena.TryWriteProvenance(
                    key: a,
                    provenance: "issuer",
                    rowOrdinal: ArenaFixture.Tokens
                ));

                break;
            case "visibility":
                Assert.True(condition: arena.TryWriteVisibility(
                    key: a,
                    rowOrdinal: ArenaFixture.Tokens,
                    visibility: new StateVisibility(Readers: ["p1"])
                ));

                break;
            case "observation":
                Assert.True(condition: arena.TryWriteObservation(
                    key: a,
                    observation: new StateObservation(
                        Tick: 12L,
                        Visible: true
                    ),
                    rowOrdinal: ArenaFixture.Tokens
                ));

                break;
            case "presence":
                Assert.True(condition: arena.TryRemove(
                    key: a,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Tokens
                ));

                break;
            case "member":
                Assert.True(condition: arena.TryMint(
                    key: out _,
                    name: ArenaFixture.Name(value: "minted"),
                    reason: out _,
                    rowOrdinal: ArenaFixture.Hand,
                    value: CellValue.Int(value: 3L)
                ));

                break;
            case "membership":
                Assert.True(condition: arena.TryTransferEnd(
                    fromOrdinal: ArenaFixture.Deck,
                    insertFirst: false,
                    key: out _,
                    reason: out _,
                    takeFirst: true,
                    toOrdinal: ArenaFixture.Hand
                ));

                break;
            case "ring":
                Assert.True(condition: arena.TryPush(
                    reason: out _,
                    rowOrdinal: ArenaFixture.History,
                    value: 42L
                ));

                break;
            case "board":
                Assert.True(condition: arena.TryWrite(
                    key: a,
                    operand: 3L,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Tokens,
                    write: StateWriteKind.Set
                ));

                break;
            case "drawCursor":
                Assert.True(condition: arena.TryWriteDrawCursor(
                    cursor: 5L,
                    rowOrdinal: ArenaFixture.Deal
                ));

                break;
            case "drawnMask":
                Assert.True(condition: arena.TryWriteDrawnMask(
                    index: 1,
                    mask: new ClosedBitset256(
                        Word0: 3UL,
                        Word1: 0UL,
                        Word2: 0UL,
                        Word3: 8UL
                    ),
                    rowOrdinal: ArenaFixture.Deal
                ));

                break;
            case "phaseSequence":
                Assert.True(condition: arena.TryWritePhaseSequence(
                    rowOrdinal: ArenaFixture.Turn,
                    sequence: 6L
                ));

                break;
            case "laneRoster":
                Assert.True(condition: arena.TryJoin(
                    lane: StateLane.Participant,
                    ordinal: 0,
                    reason: out _
                ));

                break;
            default:
                var ordinal = 0;

                Assert.True(condition: arena.TryJoin(
                    lane: StateLane.Participant,
                    ordinal: ordinal,
                    reason: out _
                ));
                Assert.True(condition: arena.TryWriteSlot(
                    operand: 4L,
                    ordinal: ordinal,
                    reason: out _,
                    rowOrdinal: ArenaFixture.Coins,
                    write: StateWriteKind.Set
                ));

                break;
        }
    }
}
