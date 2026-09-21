using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a <see cref="StateArena"/> scope records every column it writes, so a rewind
/// restores all of them — values, presence, membership, cursors, clocks, provenance, visibility, observation,
/// behavior, drawn masks, vectors, and the lane roster — and a commit keeps them.</summary>
public sealed class ArenaScopeLawTests {
    [Fact]
    public void RewindRestoresEveryColumnTheScopeWrote() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var before = Snapshot(
            arena: arena,
            catalog: catalog
        );
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.True(condition: arena.TryWriteText(
            key: slot,
            reason: out _,
            rowOrdinal: ArenaFixture.Label,
            text: "bye"
        ));
        Assert.True(condition: arena.TryWriteVector(
            components: ArenaFixture.Unit(axis: 3).Components,
            key: slot,
            reason: out _,
            rowOrdinal: ArenaFixture.Embed
        ));
        Assert.True(condition: arena.TryWriteClock(
            epochEngineTick: 4L,
            epochTick: 3L,
            key: a,
            reason: out _,
            rowOrdinal: ArenaFixture.Tokens,
            substepTicks: 7L,
            v0: 6L,
            y0: 5L
        ));
        Assert.True(condition: arena.TryWriteProvenance(
            key: a,
            provenance: "issuer",
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: a,
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryWriteVisibility(
            key: a,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: ["p1"])
        ));
        Assert.True(condition: arena.TryWriteObservation(
            key: a,
            observation: new StateObservation(
                Tick: 12L,
                Visible: true
            ),
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryPush(
            reason: out _,
            rowOrdinal: ArenaFixture.History,
            value: 42L
        ));
        Assert.True(condition: arena.TryWriteDrawCursor(
            cursor: 17L,
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.True(condition: arena.TryWriteDrawnMask(
            index: 1,
            mask: new ClosedBitset256(Word0: 0b1011UL),
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.True(condition: arena.TryWritePhaseSequence(
            rowOrdinal: ArenaFixture.Turn,
            sequence: 6L
        ));
        Assert.True(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "c"),
            reason: out _,
            rowOrdinal: ArenaFixture.Tokens,
            value: CellValue.Int(value: 3L)
        ));
        var participant = 0;

        Assert.True(condition: arena.TryJoin(
            lane: StateLane.Participant,
            ordinal: participant,
            reason: out _
        ));
        Assert.True(condition: arena.TryWriteSlot(
            operand: 25L,
            ordinal: participant,
            reason: out _,
            rowOrdinal: ArenaFixture.Coins,
            write: StateWriteKind.Set
        ));
        Assert.NotEqual(
            expected: before,
            actual: Snapshot(
                arena: arena,
                catalog: catalog
            )
        );

        arena.Rewind(mark: mark);

        Assert.Equal(
            expected: before,
            actual: Snapshot(
                arena: arena,
                catalog: catalog
            )
        );
    }
    [Fact]
    public void ARewoundRemovalRestoresEveryKeyToItsOwnSlot() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var b = ArenaFixture.Key(
            catalog: catalog,
            value: "b"
        );
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRemove(
            key: a,
            reason: out _,
            rowOrdinal: ArenaFixture.Tokens
        ));

        arena.Rewind(mark: mark);

        Assert.True(condition: arena.TryRead(
            key: a,
            rowOrdinal: ArenaFixture.Tokens,
            value: out var restoredA
        ));
        Assert.True(condition: arena.TryRead(
            key: b,
            rowOrdinal: ArenaFixture.Tokens,
            value: out var restoredB
        ));
        Assert.Equal(
            expected: CellValue.Int(value: 0L),
            actual: restoredA
        );
        Assert.Equal(
            expected: CellValue.Int(value: 2L),
            actual: restoredB
        );
    }
    [Fact]
    public void ARefusedFiringRewindsItsSavepointAndTheOuterCommitReadsEveryKeyCorrectly() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var b = ArenaFixture.Key(
            catalog: catalog,
            value: "b"
        );
        var firing = arena.BeginScope();
        var savepoint = arena.BeginScope();

        Assert.True(condition: arena.TryRemove(
            key: a,
            reason: out _,
            rowOrdinal: ArenaFixture.Tokens
        ));

        arena.Rewind(mark: savepoint);
        arena.Commit(mark: firing);

        Assert.True(condition: arena.TryRead(
            key: b,
            rowOrdinal: ArenaFixture.Tokens,
            value: out var restoredB
        ));
        Assert.Equal(
            expected: CellValue.Int(value: 2L),
            actual: restoredB
        );
        Assert.True(condition: arena.TryWrite(
            key: b,
            operand: 5L,
            reason: out _,
            rowOrdinal: ArenaFixture.Tokens,
            write: StateWriteKind.Set
        ));
        Assert.True(condition: arena.TryRead(
            key: a,
            rowOrdinal: ArenaFixture.Tokens,
            value: out var restoredA
        ));
        Assert.Equal(
            expected: CellValue.Int(value: 0L),
            actual: restoredA
        );
    }
    // A mark closes the scope it opened and no other: closing an outer scope while an inner one is open would
    // discard the inner scope's record without restoring it.
    [Fact]
    public void AMarkThatIsNotTheInnermostScopesIsRefusedBeforeAnythingMoves() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var initial = arena.Read(
            key: slot,
            rowOrdinal: ArenaFixture.Score
        );
        var outer = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 7L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        var inner = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 40L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.Rewind(mark: outer));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.Commit(mark: outer));
        Assert.Equal(
            expected: 2,
            actual: arena.Journal.Scopes
        );
        Assert.Equal(
            expected: CellValue.Int(value: 40L),
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );

        arena.Rewind(mark: inner);
        arena.Rewind(mark: outer);

        Assert.Equal(
            expected: 0,
            actual: arena.Journal.Scopes
        );
        Assert.Equal(
            expected: initial,
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );
    }
    // A cell's runtime state rides its value, so a cell that is addressable but holds none takes no clock,
    // behavior, provenance, visibility, or observation that an export would then drop.
    [Fact]
    public void ACellHoldingNoValueTakesNoRuntimeState() {
        var (catalog, arena) = ArenaFixture.Build();
        var empty = catalog.Keys.Intern(name: ArenaFixture.Name(value: "0"));
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryRead(
            key: empty,
            rowOrdinal: ArenaFixture.History,
            value: out _
        ));
        Assert.False(condition: arena.TryWriteClock(
            epochEngineTick: 4L,
            epochTick: 3L,
            key: empty,
            reason: out _,
            rowOrdinal: ArenaFixture.History,
            substepTicks: 0L,
            v0: 0L,
            y0: 0L
        ));
        Assert.False(condition: arena.TryWriteProvenance(
            key: empty,
            provenance: "issuer",
            rowOrdinal: ArenaFixture.History
        ));
        Assert.False(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: empty,
            rowOrdinal: ArenaFixture.History
        ));
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
        Assert.Equal(
            actual: (arena.ToRows()[ArenaFixture.History].Cells?.Count ?? 0),
            expected: 0
        );
    }
    // A closed scope keeps no reference to what it recorded, whichever way it closed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AClosedScopeRetainsNoRecordedReference(bool commit) {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWriteText(
            key: slot,
            reason: out _,
            rowOrdinal: ArenaFixture.Label,
            text: "bye"
        ));
        Assert.True(condition: arena.TryWriteProvenance(
            key: slot,
            provenance: "issuer",
            rowOrdinal: ArenaFixture.Label
        ));

        var recorded = arena.Journal.Length;

        Assert.True(condition: (recorded >= 2));

        if (commit) {
            arena.Commit(mark: mark);
        } else {
            arena.Rewind(mark: mark);
        }

        Assert.Equal(
            expected: 0,
            actual: arena.Journal.Length
        );

        for (var index = 0; (index < recorded); index++) {
            Assert.Null(@object: arena.Journal[index].Reference);
        }
    }
    [Fact]
    public void ACommittedScopeKeepsItsWrites() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Commit(mark: mark);

        Assert.Equal(
            expected: CellValue.Int(value: 9L),
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );
    }
    [Fact]
    public void AnInnerScopeRewindsWithinAnOuterOneThatStillCommits() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var outer = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 7L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        var inner = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 40L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Rewind(mark: inner);

        Assert.Equal(
            expected: CellValue.Int(value: 7L),
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );

        arena.Commit(mark: outer);

        Assert.Equal(
            expected: CellValue.Int(value: 7L),
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );
        Assert.Equal(
            expected: 1UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );
    }
    [Fact]
    public void ACellWrittenTwiceInOneScopeReturnsToWhatItHeldBeforeTheFirstWrite() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 7L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 8L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Rewind(mark: mark);

        Assert.Equal(
            expected: CellValue.Int(value: 5L),
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )
        );
    }

    // Every column a law could touch, folded into one comparable string so a restored arena is compared whole
    // rather than field by field.
    private static string Snapshot(StateArena arena, StateCatalog catalog) {
        var text = new System.Text.StringBuilder();

        for (var ordinal = 0; (ordinal < arena.Layout.RowCount); ordinal++) {
            var layout = arena.Layout[ordinal];

            text.Append(value: ordinal)
                .Append(value: ':')
                .Append(value: arena.CellCount(rowOrdinal: ordinal))
                .Append(value: '/')
                .Append(value: arena.HistoryCursor(rowOrdinal: ordinal))
                .Append(value: '/')
                .Append(value: arena.DrawCursor(rowOrdinal: ordinal))
                .Append(value: '/')
                .Append(value: arena.PhaseSequence(rowOrdinal: ordinal))
                .Append(value: '/')
                .Append(value: arena.DrawnMask(
                index: 1,
                rowOrdinal: ordinal
            ).ToString())
                .Append(value: '|');

            for (var position = 0; (position < layout.CellCapacity); position++) {
                if (arena.TryReadAt(
                    position: position,
                    rowOrdinal: ordinal,
                    value: out var value
                )) {
                    text.Append(value: value.ToString());

                    if (value.Kind == CellKind.Vector) {
                        foreach (var component in value.AsVector.Span) {
                            text.Append(value: component);
                        }
                    }
                }

                if (arena.TryKeyAt(
                    key: out var key,
                    position: position,
                    rowOrdinal: ordinal
                )) {
                    text.Append(value: arena.Keys[key].Value)
                        .Append(value: arena.Behavior(
                        key: key,
                        rowOrdinal: ordinal
                    ))
                        .Append(value: (arena.Provenance(
                        key: key,
                        rowOrdinal: ordinal
                    ) ?? "-"))
                        .Append(value: (arena.Visibility(
                        key: key,
                        rowOrdinal: ordinal
                    )?.ToString() ?? "-"))
                        .Append(value: (arena.Observation(
                        key: key,
                        rowOrdinal: ordinal
                    )?.ToString() ?? "-"));

                    if (arena.TryReadClock(
                        epochEngineTick: out var epochEngineTick,
                        epochTick: out var epochTick,
                        key: key,
                        rowOrdinal: ordinal,
                        set: out _,
                        substepTicks: out var substepTicks,
                        v0: out var v0,
                        y0: out var y0
                    )) {
                        text.Append(value: epochTick)
                            .Append(value: epochEngineTick)
                            .Append(value: y0)
                            .Append(value: v0)
                            .Append(value: substepTicks);
                    }
                }

                text.Append(value: ',');
            }

            text.Append(value: ';');
        }

        for (var ordinal = 0; (ordinal < 4); ordinal++) {
            text.Append(value: arena.IsJoined(
                lane: StateLane.Participant,
                ordinal: ordinal
            ));

            if (arena.TryReadSlot(
                ordinal: ordinal,
                rowOrdinal: ArenaFixture.Coins,
                value: out var coins
            )) {
                text.Append(value: coins.ToString());
            }
        }

        return text.ToString();
    }
}
