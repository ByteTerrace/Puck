using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

public sealed class ArenaScopeSnapshotTests {
    [InlineData(0)]
    [InlineData(1)]
    [Theory]
    public void AGrowingKeyedRowRestoresItsCapturedReservationAndContents(int initialCells) {
        var section = new StateSection(Rows: [
            new StateRow(Name: CellName.Parse("selected"), Kind: CellKind.Int, Domain: StateDomain.Keys.Instance, Visibility: new StateVisibility(Readers: ["seat1"]),
                CellVisibility: true, Cells: Enumerable.Range(0, initialCells).Select(index => new StateCell(CellName.Parse($"seed{index}"), CellValue.Int(3))).ToArray()),
            new StateRow(Name: CellName.Parse("outside"), Kind: CellKind.Int, Domain: StateDomain.Keys.Instance)
        ]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section), section: section, time: ArenaTime.Origin);
        var image = arena.CaptureScope(new HashSet<int> { 0 });
        var initial = Hash(arena, 0);
        Assert.Equal(initialCells, arena.Layout[0].CellCapacity);
        var key = arena.Keys.Intern(CellName.Parse("new"));
        Assert.True(arena.TryWriteOrMint(rowOrdinal: 0, key: key, value: CellValue.Int(7), evicted: out _, reason: out var reason), reason);
        Assert.True(arena.Layout[0].CellCapacity > initialCells);
        Assert.True(arena.TryWriteOrMint(rowOrdinal: 1, key: key, value: CellValue.Int(19), evicted: out _, reason: out reason), reason);
        var outside = Hash(arena, 1);
        Assert.True(arena.TryWriteVisibility(rowOrdinal: 0, key: key, visibility: new StateVisibility(Readers: ["seat1"])));
        var beforeInvalidRestore = arena.ComputeHash();
        Assert.Throws<InvalidOperationException>(() => arena.RestoreScope(image with { Numbers = image.Numbers.Skip(1).ToArray() }));
        Assert.Equal(beforeInvalidRestore, arena.ComputeHash());

        arena.ValidateScope(image);
        arena.RestoreScope(image);
        Assert.Equal(initialCells, arena.Layout[0].CellCapacity);
        Assert.Equal(initial, Hash(arena, 0));
        Assert.Equal(outside, Hash(arena, 1));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("new"), out _));
        var restoredBytes = arena.Bytes;
        Assert.True(arena.TryWriteOrMint(rowOrdinal: 0, key: key, value: CellValue.Int(7), evicted: out _, reason: out reason), reason);
        Assert.True(arena.TryWriteVisibility(rowOrdinal: 0, key: key, visibility: new StateVisibility(Readers: ["seat1"])));
        arena.RestoreScope(image);
        Assert.Equal(initial, Hash(arena, 0));
        Assert.Equal(restoredBytes, arena.Bytes);
    }

    [Fact]
    public void AReplayScopeSurvivesStorageGrowthInAnotherRow() {
        var section = new StateSection(Rows: [
            new StateRow(Name: ArenaFixture.Name(value: "outside"), Kind: CellKind.Int, Domain: StateDomain.Keys.Instance),
            new StateRow(Name: ArenaFixture.Name(value: "selected"), Kind: CellKind.Int, Capacity: 4)
        ]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
        var key = arena.Keys.Intern(name: CellName.Parse(candidate: "coins"));

        Assert.True(condition: arena.TryWriteOrMint(rowOrdinal: 1, key: key, value: CellValue.Int(value: 3), evicted: out _, reason: out _));
        var image = arena.CaptureScope(rows: new HashSet<int> { 1 });
        var initial = Hash(arena, 1);
        var start = arena.Layout[1].CellStart;
        var capacity = (arena.Layout[0].CellCapacity + 1);

        Assert.True(condition: arena.TryReserveCells(reason: out var reason, required: capacity, rowOrdinal: 0), userMessage: reason);
        Assert.NotEqual(start, arena.Layout[1].CellStart);
        Assert.True(condition: arena.TryWrite(key: key, operand: 9, reason: out _, rowOrdinal: 1, write: StateWriteKind.Set));
        arena.RestoreScope(image: image);
        Assert.Equal(initial, Hash(arena, 1));
        Assert.True(condition: (arena.Layout[0].CellCapacity >= capacity));
    }

    private static ulong Hash(StateArena arena, params int[] rows) {
        var hash = Fnv1aHash.Create();

        foreach (var row in rows) { arena.AddRowTo(hash: ref hash, rowOrdinal: row); }
        return hash.Value;
    }

    [Fact]
    public void RestoringRowsRetainsTheirCursorsAndTextWhileOtherRowsAndNewKeysStayLive() {
        var (_, arena) = ArenaFixture.Build();
        var key = arena.Keys.Intern(name: StateRow.SlotKey);
        var selected = new HashSet<int> { ArenaFixture.Score, ArenaFixture.History, ArenaFixture.Label };
        var initial = Hash(arena, ArenaFixture.Score, ArenaFixture.History, ArenaFixture.Label);
        var image = arena.CaptureScope(rows: selected);

        Assert.True(condition: arena.TryWrite(key: key, operand: 17, reason: out _, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set));
        Assert.True(condition: arena.TryWriteText(key: key, reason: out _, rowOrdinal: ArenaFixture.Label, text: "changed"));
        Assert.True(condition: arena.TryWriteHistoryCursor(cursor: 19, rowOrdinal: ArenaFixture.History));
        var token = arena.Keys.Intern(name: CellName.Parse(candidate: "a"));

        Assert.True(condition: arena.TryWrite(key: token, operand: 3, reason: out _, rowOrdinal: ArenaFixture.Codes, write: StateWriteKind.Set));
        var newKey = arena.Keys.Intern(name: CellName.Parse(candidate: "outside-new-key"));

        arena.RestoreScope(image: image);
        Assert.Equal(initial, Hash(arena, ArenaFixture.Score, ArenaFixture.History, ArenaFixture.Label));
        Assert.True(condition: arena.TryRead(key: token, rowOrdinal: ArenaFixture.Codes, value: out var code));
        Assert.Equal(3, code.Raw);
        Assert.True(condition: arena.Keys.TryResolve(CellName.Parse(candidate: "outside-new-key"), out var retained));
        Assert.Equal(actual: retained, expected: newKey);

        var before = arena.ComputeHash();
        var outside = image with { Numbers = [.. image.Numbers, new(Column: ArenaColumn.Number, Index: arena.Layout[ArenaFixture.Codes].CellStart, Value: 9)] };

        Assert.Throws<InvalidOperationException>(testCode: () => arena.RestoreScope(image: outside));
        Assert.Equal(before, arena.ComputeHash());
    }
    [Fact]
    public void AnIncompleteOrDuplicateScopeColumnImageIsRefusedWithoutChangingAnyRow() {
        var (_, arena) = ArenaFixture.Build();
        var image = arena.CaptureScope(rows: new HashSet<int> { ArenaFixture.Score });
        var before = arena.ComputeHash();

        Assert.Throws<InvalidOperationException>(testCode: () => arena.RestoreScope(image: image with { Numbers = image.Numbers.Skip(count: 1).ToArray() }));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.RestoreScope(image: image with { Numbers = [.. image.Numbers, image.Numbers[0]] }));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.RestoreScope(image: image with {
            Numbers = [.. image.Numbers, new ArenaNumberImage(Column: ArenaColumn.CellCapacity, Index: ArenaFixture.Score, Value: 999)],
        }));
        Assert.Equal(before, arena.ComputeHash());
    }
    // A scope image read from a checkpoint passes the same position admission as a full checkpoint and an undo turn.
    [InlineData(ArenaColumn.MemberKey, long.MaxValue)]
    [InlineData(ArenaColumn.Presence, 2L)]
    [Theory]
    public void AScopeImageCarryingAValueNoArenaHoldsIsRefusedWithoutChangingAnyRow(ArenaColumn column, long value) {
        var (_, arena) = ArenaFixture.Build();
        var image = arena.CaptureScope(rows: new HashSet<int> { ArenaFixture.Score });
        var before = arena.ComputeHash();
        var at = Array.FindIndex(array: image.Numbers, match: entry => (entry.Column == column));

        Assert.True(condition: (at >= 0));
        var numbers = image.Numbers.ToArray();
        numbers[at] = (numbers[at] with { Value = value });
        Assert.Throws<InvalidOperationException>(testCode: () => arena.ValidateScope(image: image with { Numbers = numbers }));
        Assert.Throws<InvalidOperationException>(testCode: () => arena.RestoreScope(image: image with { Numbers = numbers }));
        Assert.Equal(before, arena.ComputeHash());
    }
    [InlineData(ArenaFixture.Clock)]
    [InlineData(ArenaFixture.Embed)]
    [InlineData(ArenaFixture.Field)]
    [InlineData(ArenaFixture.Coins)]
    [Theory]
    public void ARowNeedingAWiderContinuationIsRefused(int row) {
        var (_, arena) = ArenaFixture.Build();
        Assert.Throws<InvalidOperationException>(testCode: () => arena.CaptureScope(rows: new HashSet<int> { row }));
    }
}
