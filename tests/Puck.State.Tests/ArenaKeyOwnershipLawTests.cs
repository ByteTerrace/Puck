using Xunit;

namespace Puck.State.Tests;

/// <summary>THE LAW: compiled keys, arena-local runtime keys, journal key epochs, and import admission keep their
/// ownership boundaries. A speculative key is disposable, a committed key survives nested scopes, and a stale
/// handle never addresses a later mint that reused its ordinal.</summary>
public sealed class ArenaKeyOwnershipLawTests {
    [Fact]
    public void CompiledKeysReadAndRuntimeKeysStayLocalToTheArena() {
        var (catalog, arena) = ArenaFixture.Build();
        var compiled = ArenaFixture.Key(catalog: catalog, value: "a");

        Assert.True(condition: arena.TryRead(key: compiled, rowOrdinal: ArenaFixture.Tokens, value: out _));
        Assert.True(condition: arena.TryMint(
            rowOrdinal: ArenaFixture.Tokens,
            name: ArenaFixture.Name(value: "runtime-only"),
            value: CellValue.Int(value: 3L),
            key: out var runtime,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: arena.TryRead(key: runtime, rowOrdinal: ArenaFixture.Tokens, value: out _));
        Assert.False(condition: catalog.Keys.TryResolve(ArenaFixture.Name(value: "runtime-only"), out _));
    }
    [Fact]
    public void NestedCommitKeepsKeysAndSiblingRewindDoesNotLeakThem() {
        var (_, arena) = ArenaFixture.Build();
        var outer = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Tokens, ArenaFixture.Name(value: "outer"), CellValue.Int(value: 1L), out _, out var reason), userMessage: reason);

        var inner = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Tokens, ArenaFixture.Name(value: "inner"), CellValue.Int(value: 1L), out _, out reason), userMessage: reason);
        arena.Commit(mark: inner);

        var sibling = arena.BeginScope();

        _ = arena.Keys.Intern(name: ArenaFixture.Name(value: "sibling"));
        arena.Rewind(mark: sibling);

        Assert.True(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "outer"), out _));
        Assert.True(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "inner"), out _));
        Assert.False(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "sibling"), out _));

        arena.Rewind(mark: outer);
        Assert.False(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "outer"), out _));
        Assert.False(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "inner"), out _));
    }
    [Fact]
    public void KeyOnlyScopeRewindReleasesNamesAndEpochRejectsOrdinalReuse() {
        var (_, arena) = ArenaFixture.Build();
        var before = arena.Keys.Count;
        var mark = arena.BeginScope();
        var stale = arena.Keys.Intern(name: ArenaFixture.Name(value: "key-only"));

        Assert.Equal((before + 1), arena.Keys.Count);

        arena.Rewind(mark: mark);
        Assert.Equal(before, arena.Keys.Count);
        Assert.False(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "key-only"), out _));

        var reused = arena.Keys.Intern(name: ArenaFixture.Name(value: "key-only"));

        Assert.NotEqual(actual: reused, expected: stale);
        Assert.False(condition: arena.TryRead(key: stale, rowOrdinal: ArenaFixture.Tokens, value: out _));
    }
    [Fact]
    public void RewoundKeysDoNotSurviveRingAddressCaches() {
        var section = new StateSection(
            Rows: [
                new StateRow(
                    Name: ArenaFixture.Name(value: "ring"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.Ring(Capacity: 2)
                )
            ]
        );
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var mark = arena.BeginScope();
        var stale = arena.Keys.Intern(name: ArenaFixture.Name(value: "0"));

        Assert.True(condition: arena.TryCellSlot(key: stale, rowOrdinal: 0, slot: out var staleSlot));
        arena.Rewind(mark: mark);

        var current = arena.Keys.Intern(name: ArenaFixture.Name(value: "1"));

        Assert.NotEqual(actual: current, expected: stale);
        Assert.False(condition: arena.TryCellSlot(key: stale, rowOrdinal: 0, slot: out _));
        Assert.True(condition: arena.TryCellSlot(key: current, rowOrdinal: 0, slot: out var currentSlot));
        Assert.NotEqual(actual: currentSlot, expected: staleSlot);
    }
    [Fact]
    public void SequentialRewoundNamesDoNotExhaustTheKeyCeiling() {
        var (_, arena) = ArenaFixture.Build();
        var initial = arena.Keys.Count;

        for (var index = 0; (index <= StateCapacity.MaxCellKeys); index++) {
            var mark = arena.BeginScope();

            Assert.True(condition: arena.Keys.TryIntern(
                ArenaFixture.Name(value: $"rewound-{index}"),
                out _,
                out var reason
            ), userMessage: reason);
            arena.Rewind(mark: mark);
        }

        Assert.Equal(initial, arena.Keys.Count);
    }
    [Fact]
    public void LateCatalogKeysTranslateByNameBeforeAndAfterRuntimeMint() {
        var (catalog, arena) = ArenaFixture.Build();

        var compiledBefore = catalog.Keys.Intern(name: ArenaFixture.Name(value: "late-before"));
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Tokens, ArenaFixture.Name(value: "late-before"), CellValue.Int(value: 1L), out _, out var reason), userMessage: reason);
        Assert.True(condition: arena.TryRead(key: compiledBefore, rowOrdinal: ArenaFixture.Tokens, value: out _));
        arena.Rewind(mark: mark);

        var second = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Tokens, ArenaFixture.Name(value: "late-after"), CellValue.Int(value: 1L), out var runtime, out reason), userMessage: reason);
        var compiledAfter = catalog.Keys.Intern(name: ArenaFixture.Name(value: "late-after"));

        Assert.True(condition: arena.TryRead(key: runtime, rowOrdinal: ArenaFixture.Tokens, value: out _));
        Assert.True(condition: arena.TryRead(key: compiledAfter, rowOrdinal: ArenaFixture.Tokens, value: out _));
        arena.Rewind(mark: second);
    }
    [Fact]
    public void LateCatalogKeysTranslateThroughRemoveAndTransfer() {
        var (catalog, arena) = ArenaFixture.Build();
        var removeScope = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Bag, ArenaFixture.Name(value: "late-remove"), CellValue.Int(value: 1L), out _, out var reason), userMessage: reason);
        var removeKey = catalog.Keys.Intern(name: ArenaFixture.Name(value: "late-remove"));

        Assert.True(condition: arena.TryRemove(key: removeKey, reason: out reason, rowOrdinal: ArenaFixture.Bag), userMessage: reason);
        arena.Rewind(mark: removeScope);

        var transferScope = arena.BeginScope();

        Assert.True(condition: arena.TryMint(ArenaFixture.Bag, ArenaFixture.Name(value: "late-transfer"), CellValue.Int(value: 1L), out _, out reason), userMessage: reason);
        var transferKey = catalog.Keys.Intern(name: ArenaFixture.Name(value: "late-transfer"));

        Assert.True(condition: arena.TryTransfer(
            fromOrdinal: ArenaFixture.Bag,
            insertFirst: true,
            key: transferKey,
            reason: out reason,
            toOrdinal: ArenaFixture.Hand
        ), userMessage: reason);
        arena.Rewind(mark: transferScope);
    }
    [Fact]
    public void RefusedImportPreservesKeyCount() {
        var (_, arena) = ArenaFixture.Build();
        var before = arena.Keys.Count;
        var refused = new StateRow(
            Name: ArenaFixture.Name(value: "tokens"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [
                new StateCell(ArenaFixture.Name(value: "refused-a"), CellValue.Int(value: 1L)),
                new StateCell(ArenaFixture.Name(value: "refused-a"), CellValue.Int(value: 1L))
            ]
        );

        Assert.False(condition: arena.TryLoad([refused], ArenaTime.Origin, out _));
        Assert.Equal(before, arena.Keys.Count);
        Assert.False(condition: arena.Keys.TryResolve(ArenaFixture.Name(value: "refused-a"), out _));
    }
    [Fact]
    public void LateCatalogKeyAtLocalCeilingCannotBypassMemberAdmission() {
        var (catalog, arena) = ArenaFixture.Build();
        var late = ArenaFixture.Name(value: "late-at-ceiling");

        _ = catalog.Keys.Intern(name: late);
        while (arena.Keys.Count < StateCapacity.MaxCellKeys) {
            Assert.True(condition: arena.Keys.TryIntern(
                ArenaFixture.Name(value: $"filled-{arena.Keys.Count}"),
                out _,
                out var fillReason
            ), userMessage: fillReason);
        }

        var membersBefore = arena.CellCount(rowOrdinal: ArenaFixture.Bag);

        Assert.False(condition: arena.TryMint(
            rowOrdinal: ArenaFixture.Bag,
            name: late,
            value: CellValue.Int(value: 1L),
            key: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "key");
        Assert.Equal(membersBefore, arena.CellCount(rowOrdinal: ArenaFixture.Bag));
        Assert.Equal(StateCapacity.MaxCellKeys, arena.Keys.Count);
    }
    [Fact]
    public void ConstructionAndRelayoutRefuseAnIncompatibleInverseDomain() {
        var source = ArenaFixture.Section();
        var rows = source.Rows!.ToArray();

        rows[ArenaFixture.Board] = rows[ArenaFixture.Board] with { Min = -1L, Max = 1L };
        rows[ArenaFixture.Codes] = rows[ArenaFixture.Codes] with { Min = 0L, Max = 2L };
        var incompatible = source with { Rows = rows };
        var catalog = StateCatalog.Compile(section: incompatible);

        Assert.False(condition: StateArena.TryCreate(
            catalog: catalog,
            section: incompatible,
            options: null,
            time: ArenaTime.Origin,
            arena: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "inverse codes");

        var (_, arena) = ArenaFixture.Build();
        var before = arena.Keys.Count;

        Assert.False(condition: arena.TryRelayout(
            catalog: catalog,
            section: incompatible,
            time: ArenaTime.Origin,
            reason: out reason
        ));
        Assert.Equal(before, arena.Keys.Count);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "inverse codes");
    }
}
