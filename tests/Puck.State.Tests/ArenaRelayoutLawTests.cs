using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a relayout carries every value across by row name and cell key, including a
/// re-declaration that reassigns the ordinals its rows had, refuses a kind change by name, refuses while a journal
/// scope is open, and leaves the arena untouched whenever it refuses.</summary>
public sealed class ArenaRelayoutLawTests {
    // A key interned by the catalog a relayout replaced addresses nothing at any door, including the ones that
    // remove or move a member.
    [Fact]
    public void AKeyFromTheReplacedCatalogRemovesAndMovesNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        var stale = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var section = ArenaFixture.Section();

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: section),
            reason: out var reason,
            section: section,
            time: ArenaTime.Origin
        ), userMessage: reason);

        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryRemove(
            key: stale,
            reason: out reason,
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not resolve"
        );
        Assert.False(condition: arena.TryTransfer(
            fromOrdinal: ArenaFixture.Deck,
            insertFirst: false,
            key: stale,
            reason: out reason,
            toOrdinal: ArenaFixture.Hand
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not resolve"
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Tokens),
            expected: 2
        );
    }
    [Fact]
    public void ARelayoutCarriesEveryValueAcrossByRowNameAndKey() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        ArenaSerializationLawTests.Populate(
            arena: arena,
            catalog: catalog
        );

        var carried = arena.ToRows();
        var widened = Widen(section: section);
        var widenedCatalog = StateCatalog.Compile(section: widened);

        Assert.True(condition: arena.TryRelayout(
            catalog: widenedCatalog,
            reason: out var reason,
            section: widened,
            time: ArenaTime.Origin
        ), userMessage: reason);
        Assert.Same(
            actual: arena.Catalog,
            expected: widenedCatalog
        );

        var exported = arena.ToRows();

        Assert.Equal(
            actual: exported.Count,
            expected: (carried.Count + 1)
        );
        ArenaSerializationLawTests.AssertSameRows(
            actual: exported.Take(count: carried.Count).ToList(),
            expected: carried
        );

        // The row only the new declaration carries holds what the section seeds it with.
        Assert.Equal(
            actual: arena.Read(
                key: ArenaFixture.SlotKey(catalog: arena.Catalog),
                rowOrdinal: (exported.Count - 1)
            )!.Value.AsInt,
            expected: 3L
        );
    }
    [Fact]
    public void ARelayoutMovesEveryCounterAboveWhereItWas() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: arena.TryWrite(
            key: ArenaFixture.SlotKey(catalog: catalog),
            operand: 9L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var version = arena.RowVersion(rowOrdinal: ArenaFixture.Score);
        var widened = Widen(section: section);

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: widened),
            reason: out reason,
            section: widened,
            time: ArenaTime.Origin
        ), userMessage: reason);
        Assert.True(condition: (arena.RowVersion(rowOrdinal: ArenaFixture.Score) > version));
        Assert.True(condition: (arena.RowGeneration(rowOrdinal: ArenaFixture.Score) > 0UL));
    }
    [Fact]
    public void ARelayoutKeepsTheLaneRosterAndEveryLaneSlotValue() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        var participant = 0;

        Assert.True(condition: arena.TryJoin(
            lane: StateLane.Participant,
            ordinal: participant,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteSlot(
            operand: 4L,
            ordinal: participant,
            reason: out reason,
            rowOrdinal: ArenaFixture.Coins,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var widened = Widen(section: section);

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: widened),
            reason: out reason,
            section: widened,
            time: ArenaTime.Origin
        ), userMessage: reason);
        Assert.True(condition: arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: participant
        ));
        Assert.True(condition: arena.TryReadSlot(
            ordinal: participant,
            rowOrdinal: (ArenaFixture.Coins + 1),
            value: out var coins
        ));
        Assert.Equal(
            actual: coins.AsFixed,
            expected: 4L
        );
    }
    // The routine a relayout carries the lanes with is the same one a host replacing its arena outright reaches
    // for, so a fresh build is not a second, lossy path beside it.
    [Fact]
    public void AFreshlyBuiltArenaInheritsTheLaneRosterAndEveryLaneSlotValue() {
        var section = ArenaFixture.Section();
        var arena = new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var participant = 2;

        Assert.True(condition: arena.TryJoin(
            lane: StateLane.Participant,
            ordinal: participant,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteSlot(
            operand: 4L,
            ordinal: participant,
            reason: out reason,
            rowOrdinal: ArenaFixture.Coins,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var replacement = new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        // Control: the replacement starts with nobody joined, so the assertions below discriminate the copy.
        Assert.False(condition: replacement.IsJoined(
            lane: StateLane.Participant,
            ordinal: participant
        ));

        arena.CopyLanesTo(target: replacement);

        Assert.True(condition: replacement.IsJoined(
            lane: StateLane.Participant,
            ordinal: participant
        ));
        Assert.True(condition: replacement.TryReadSlot(
            ordinal: participant,
            rowOrdinal: ArenaFixture.Coins,
            value: out var coins
        ));
        Assert.Equal(
            actual: coins.AsFixed,
            expected: 4L
        );
        // An ordinal the source never admitted stays unjoined, so the copy carries the roster rather than filling it.
        Assert.False(condition: replacement.IsJoined(
            lane: StateLane.Participant,
            ordinal: (participant + 1)
        ));
    }
    [Fact]
    public void ARelayoutRefusesAKindChangeByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rows = new List<StateRow>(collection: section.Rows!);

        rows[ArenaFixture.Score] = new StateRow(
            Name: ArenaFixture.Name(value: "score"),
            Kind: CellKind.Text
        );

        var retyped = (section with { Rows = rows });

        Assert.False(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: retyped),
            reason: out reason,
            section: retyped,
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "score"
        );
        Assert.Same(
            actual: arena.Catalog,
            expected: catalog
        );
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )!.Value.AsInt,
            expected: 9L
        );
    }
    [Fact]
    public void ARelayoutRefusesWhileAJournalScopeIsOpen() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var widened = Widen(section: section);
        var mark = arena.BeginScope();

        Assert.False(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: widened),
            reason: out var reason,
            section: widened,
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "scope"
        );

        arena.Commit(mark: mark);

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: widened),
            reason: out reason,
            section: widened,
            time: ArenaTime.Origin
        ), userMessage: reason);
    }
    [Fact]
    public void ARelayoutRefusesAValueTheRedeclaredRowWouldNotAdmitByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 90L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rows = new List<StateRow>(collection: section.Rows!);

        rows[ArenaFixture.Score] = (rows[ArenaFixture.Score] with { Max = 10L });

        var narrowed = (section with { Rows = rows });

        Assert.False(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: narrowed),
            reason: out reason,
            section: narrowed,
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "score"
        );
        Assert.Equal(
            actual: arena.Read(
                key: slot,
                rowOrdinal: ArenaFixture.Score
            )!.Value.AsInt,
            expected: 90L
        );
    }
    [Fact]
    public void ARelayoutDropsARowTheNewDeclarationNoLongerCarries() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var rows = new List<StateRow>(collection: section.Rows!);

        rows.RemoveAt(index: ArenaFixture.Clock);

        var narrowed = (section with { Rows = rows });

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: narrowed),
            reason: out var reason,
            section: narrowed,
            time: ArenaTime.Origin
        ), userMessage: reason);
        Assert.DoesNotContain(
            collection: arena.ToRows(),
            filter: static (StateRow row) => (row.Name.Value == "clock")
        );
    }
    [Fact]
    public void ARelayoutThatReassignsEveryRowOrdinalStillCarriesEveryValueByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        ArenaSerializationLawTests.Populate(
            arena: arena,
            catalog: catalog
        );

        var carried = arena.ToRows();
        var rows = new List<StateRow>(collection: section.Rows!);
        var last = rows[^1];

        rows.RemoveAt(index: (rows.Count - 1));
        rows.Insert(
            index: 0,
            item: last
        );

        var reordered = (section with { Rows = rows });
        var reorderedCatalog = StateCatalog.Compile(section: reordered);

        Assert.True(condition: arena.TryRelayout(
            catalog: reorderedCatalog,
            reason: out var reason,
            section: reordered,
            time: ArenaTime.Origin
        ), userMessage: reason);

        var exported = arena.ToRows();

        Assert.Equal(
            actual: exported.Count,
            expected: carried.Count
        );
        Assert.NotEqual(
            actual: exported[0].Name,
            expected: carried[0].Name
        );

        foreach (var expected in carried) {
            ArenaSerializationLawTests.AssertSameRows(
                actual: [exported.Single(predicate: (StateRow row) => (row.Name == expected.Name))],
                expected: [expected]
            );
        }
    }

    private static StateSection Widen(StateSection section) {
        var rows = new List<StateRow>(collection: section.Rows!);

        rows.Add(item: new StateRow(
            Name: ArenaFixture.Name(value: "extra"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 3L)
                )]
        ));

        return (section with { Rows = rows });
    }
}
