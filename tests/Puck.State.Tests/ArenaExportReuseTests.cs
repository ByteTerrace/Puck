using Xunit;

namespace Puck.State.Tests;

public sealed class ArenaExportReuseTests {
    [Fact]
    public void ReusedExportsKeepOlderSnapshotsAndMatchIndependentExports() {
        var (_, arena) = ArenaFixture.Build();
        var before = arena.ToRow(ArenaFixture.Tokens);
        var unchanged = arena.ToRow(previous: before, rowOrdinal: ArenaFixture.Tokens);

        Assert.Same(before.Cells, unchanged.Cells);
        var oldValue = before.Cells![0].Value;

        Assert.True(condition: arena.Keys.TryResolve(before.Cells[0].Key, out var key));
        Assert.True(condition: arena.TryWrite(ArenaFixture.Tokens, key, CellValue.Int(value: 2), out var reason), userMessage: reason);
        var after = arena.ToRow(previous: before, rowOrdinal: ArenaFixture.Tokens);

        Assert.Equal(oldValue, before.Cells[0].Value);
        Assert.Equal(CellValue.Int(value: 2), after.Cells![0].Value);
        Assert.Same(before.Cells[1], after.Cells[1]);
        Assert.Equal(arena.ToRow(ArenaFixture.Tokens).Cells, after.Cells);

        // A prior export from a different row, or with fewer/more cells, is only a sharing hint.
        var unrelated = arena.ToRow(ArenaFixture.Score);

        Assert.Equal(after.Cells, arena.ToRow(previous: unrelated, rowOrdinal: ArenaFixture.Tokens).Cells);
        Assert.Equal(unrelated.Cells, arena.ToRow(previous: after, rowOrdinal: ArenaFixture.Score).Cells);
    }
}
