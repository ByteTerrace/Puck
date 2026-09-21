using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>Member names and row positions define content identity independently of intern allocation history.</summary>
public sealed class ArenaMemberHashLawTests {
    [Fact]
    public void ExportAndFreshCatalogRebuildPreserveAllMemberHashEntrances() {
        var section = Section();
        var arena = Build(section: section);

        Mint(arena: arena, name: "b", row: 1);
        Mint(arena: arena, name: "a", row: 0);
        var exported = section with { Rows = arena.ToRows() };
        var restored = Build(section: exported);

        Assert.NotEqual(expected: arena.Keys.Names[0], actual: restored.Keys.Names[0]);
        Assert.Equal(expected: arena.ComputeHash(), actual: restored.ComputeHash());
        Assert.Equal(expected: arena.ComputeColumnHash(column: ArenaColumn.MemberKey), actual: restored.ComputeColumnHash(column: ArenaColumn.MemberKey));
        for (var row = 0; (row < 2); row++) {
            var before = Fnv1aHash.Create();
            var after = Fnv1aHash.Create();

            arena.AddRowTo(hash: ref before, rowOrdinal: row);
            restored.AddRowTo(hash: ref after, rowOrdinal: row);
            Assert.Equal(expected: before.Value, actual: after.Value);
        }
    }
    [Fact]
    public void DifferentNamesAtTheSameOrdinalHaveDifferentContentHashes() {
        var first = Build(section: Section());
        var second = Build(section: Section());

        Mint(arena: first, name: "a", row: 0);
        Mint(arena: second, name: "b", row: 0);
        Assert.NotEqual(expected: first.ComputeHash(), actual: second.ComputeHash());
    }
    [Fact]
    public void MemberOrderRemainsPartOfContentIdentity() {
        var first = Build(section: Section());
        var second = Build(section: Section());

        Mint(arena: first, name: "a", row: 0);
        Mint(arena: first, name: "b", row: 0);
        Mint(arena: second, name: "b", row: 0);
        Mint(arena: second, name: "a", row: 0);
        Assert.NotEqual(expected: first.ComputeHash(), actual: second.ComputeHash());
    }

    private static StateSection Section() => new(Rows: [
        new StateRow(Name: CellName.Parse(candidate: "left"), Kind: CellKind.Int, Capacity: 4, Cells: []),
        new StateRow(Name: CellName.Parse(candidate: "right"), Kind: CellKind.Int, Capacity: 4, Cells: []),
    ]);
    private static StateArena Build(StateSection section) => new(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
    private static void Mint(StateArena arena, int row, string name) => Assert.True(condition: arena.TryMint(
        rowOrdinal: row, name: CellName.Parse(candidate: name), value: CellValue.Int(value: 7), key: out _, reason: out var reason
    ), userMessage: reason);
}
