using Puck.Abstractions.Collections;

namespace Puck.Abstractions.Tests;

/// <summary>A pair-keyed table answers by either half what a walk of every entry would: each write, replacement and
/// removal moves both halves' lists with the entry, and a half that holds nothing keeps no list.</summary>
public sealed class PairKeyedTableLawTests {
    [Fact]
    public void EachHalfListsExactlyTheEntriesAWalkWouldFind() {
        var table = new PairKeyedTable<string, int, string>();

        Assert.False(condition: table.Set(left: "a", right: 1, value: "a1", replaced: out _));
        Assert.False(condition: table.Set(left: "a", right: 2, value: "a2", replaced: out _));
        Assert.False(condition: table.Set(left: "b", right: 1, value: "b1", replaced: out _));
        Assert.True(condition: table.Set(left: "a", right: 1, value: "a1'", replaced: out var replaced));
        Assert.Equal(expected: "a1", actual: replaced);
        Assert.Equal(expected: 3, actual: table.Count);
        Assert.Equal(expected: [1, 2], actual: table.RightsOf(left: "a").Order());
        Assert.Equal(expected: ["a", "b"], actual: table.LeftsOf(right: 1).Order());
        Assert.True(condition: table.TryGetValue(left: "a", right: 1, value: out var read));
        Assert.Equal(expected: "a1'", actual: read);

        // A half's answer is a copy: removing while walking it is the intended use.
        foreach (var left in table.LeftsOf(right: 1)) {
            Assert.True(condition: table.TryRemove(left: left, right: 1, removed: out _));
        }
        Assert.Empty(collection: table.LeftsOf(right: 1));
        Assert.False(condition: table.ContainsLeft(left: "b"));
        Assert.True(condition: table.ContainsLeft(left: "a"));
        Assert.Equal(expected: [2], actual: table.RightsOf(left: "a"));
        Assert.False(condition: table.TryRemove(left: "b", right: 1, removed: out _));
        Assert.Equal(expected: ["a2"], actual: table.Clear());
        Assert.False(condition: table.ContainsLeft(left: "a"));
        Assert.Empty(collection: table.Entries);
    }
}
