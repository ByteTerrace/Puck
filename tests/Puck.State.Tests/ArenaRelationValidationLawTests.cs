using Xunit;

namespace Puck.State.Tests;

public sealed class ArenaRelationValidationLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARelationIsRecheckedWhenItsMembershipOrDomainChanges(bool domainChanged) {
        var (catalog, arena) = ArenaFixture.Build();
        // Remove a domain member while an ordered row still contains it.
        Assert.True(condition: arena.TryRemove(rowOrdinal: ArenaFixture.Tokens,
            key: ArenaFixture.Key(catalog: catalog, value: "a"), reason: out var reason), userMessage: reason);
        var changed = new bool[arena.Layout.RowCount];

        changed[ArenaFixture.Score] = true;
        Assert.True(condition: arena.TryValidateRelations(changedRows: changed, reason: out _));
        changed[(domainChanged ? ArenaFixture.Tokens : ArenaFixture.Deck)] = true;
        Assert.False(condition: arena.TryValidateRelations(changedRows: changed, reason: out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "outside token domain");
        Assert.False(condition: arena.TryValidateRelations(reason: out _));
    }
}
