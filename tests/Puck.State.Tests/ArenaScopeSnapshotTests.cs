using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

public sealed class ArenaScopeSnapshotTests {
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
