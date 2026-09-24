using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a change window answers whether a run of committed scopes left a row different from
/// what it held when the window opened, which a row version cannot answer once more than one scope has committed;
/// and closing it tests each position written once against the judged rows, so its cost follows the positions
/// written and never their product with the rows judged.</summary>
public sealed class ArenaChangeWindowLawTests {
    private static void Commit(StateArena arena, CellKey slot, long value) {
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(key: slot, operand: value, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
        arena.Commit(mark: mark);
    }
    private static CellSet Rows(StateCatalog catalog, params int[] rows) {
        var set = CellSet.Empty(length: catalog.Descriptors.Count);

        foreach (var row in rows) {
            set = set.Add(index: row);
        }

        return set;
    }
    // The membership tests every change window has made so far, read through the arena's work counter source.
    private static long Probes(IWorkCounterSource arena) {
        Assert.True(condition: arena.TryRead(kind: ArenaWork.ChangeWindowProbes, value: out var probes));

        return probes;
    }
    // Writes each named code to a new value in one commit and restores it in the next, so the window retains the
    // same positions for every code, the code's own and the derived board cells it moves, and the close finds none
    // of them changed, probing every one.
    private static int ProbesForARestoredPass(StateCatalog catalog, StateArena arena, CellSet judged, string[] codes) {
        var keys = codes.Select(selector: code => catalog.Keys.Intern(name: CellName.Parse(candidate: code))).ToArray();
        var held = keys.Select(selector: key => (arena.TryRead(key: key, rowOrdinal: ArenaFixture.Codes, value: out var value) ? value.AsInt : 0L)).ToArray();

        var before = Probes(arena: arena);

        arena.BeginChangeWindow();
        foreach (var offset in new[] { 1L, 0L }) {
            var mark = arena.BeginScope();

            for (var index = 0; (index < keys.Length); index++) {
                Assert.True(condition: arena.TryWrite(key: keys[index], operand: (held[index] + offset), reason: out var reason, rowOrdinal: ArenaFixture.Codes, write: StateWriteKind.Set), userMessage: reason);
            }
            arena.Commit(mark: mark);
        }
        Assert.False(condition: arena.EndChangeWindow(rows: judged));

        return ((int)(Probes(arena: arena) - before));
    }

    [Fact]
    public void ARowClearedAndRebuiltAcrossTwoCommitsReadsUnchangedWhileItsVersionMoved() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var version = arena.RowVersion(rowOrdinal: ArenaFixture.Score);
        var original = (arena.TryRead(key: slot, rowOrdinal: ArenaFixture.Score, value: out var held) ? held.AsInt : 0L);

        arena.BeginChangeWindow();
        Commit(arena: arena, slot: slot, value: (original + 7L));
        Commit(arena: arena, slot: slot, value: original);

        Assert.NotEqual(expected: version, actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score));
        Assert.False(condition: arena.EndChangeWindow(rows: Rows(catalog: catalog, ArenaFixture.Score)));
    }
    [Fact]
    public void ARowLeftDifferentReadsChangedOnlyWhenItIsJudged() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var original = (arena.TryRead(key: slot, rowOrdinal: ArenaFixture.Score, value: out var held) ? held.AsInt : 0L);

        arena.BeginChangeWindow();
        Commit(arena: arena, slot: slot, value: (original + 1L));
        Commit(arena: arena, slot: slot, value: (original + 2L));
        Assert.True(condition: arena.EndChangeWindow(rows: Rows(catalog: catalog, ArenaFixture.Score)));

        arena.BeginChangeWindow();
        Commit(arena: arena, slot: slot, value: (original + 3L));
        Assert.False(condition: arena.EndChangeWindow(rows: Rows(catalog: catalog, ArenaFixture.Tokens)));
    }
    [Fact]
    public void ARewoundWriteIsNotAChangeAndAWindowNeverNests() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var score = Rows(catalog: catalog, ArenaFixture.Score);

        arena.BeginChangeWindow();
        Assert.Throws<InvalidOperationException>(testCode: arena.BeginChangeWindow);

        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(key: slot, operand: 42L, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
        arena.Rewind(mark: mark);
        Assert.False(condition: arena.EndChangeWindow(rows: score));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.EndChangeWindow(rows: score));
    }
    [Fact]
    public void ClosingAWindowProbesEachPositionWrittenOnceWhateverTheWidthJudged() {
        var (catalog, arena) = ArenaFixture.Build();
        var narrow = Rows(catalog: catalog, ArenaFixture.Codes);
        var every = Rows(catalog: catalog, [.. Enumerable.Range(start: 0, count: catalog.Descriptors.Count)]);
        var one = ProbesForARestoredPass(arena: arena, catalog: catalog, codes: ["a"], judged: narrow);
        var two = ProbesForARestoredPass(arena: arena, catalog: catalog, codes: ["a", "b"], judged: narrow);

        Assert.True(condition: (one > 0));
        Assert.Equal(actual: two, expected: (2 * one));
        Assert.Equal(expected: two, actual: ProbesForARestoredPass(arena: arena, catalog: catalog, codes: ["a", "b"], judged: every));
    }
}
