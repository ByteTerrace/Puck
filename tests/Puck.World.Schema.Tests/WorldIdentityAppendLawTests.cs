using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: an owned identity's evicting text append composes through an arena over its own
/// document, so the row's capacity, its FIFO eviction and its named victim are the store's rather than a second
/// reading of the same rule.</summary>
public sealed class WorldIdentityAppendLawTests {
    private static WorldIdentity Identity(int capacity) => new(
        defaults: new WorldPlayerDefaults(),
        document: new WorldDefinition(
            Identity: new WorldIdentityDefinition(
                Color: "#ffffff",
                Id: SafeName.Parse(candidate: "scribe"),
                MoveSpeedState: CellName.Parse(candidate: "moveSpeed"),
                Name: "Scribe",
                TurnSpeedState: CellName.Parse(candidate: "turnSpeed")
            ),
            StateRaw: new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "log"),
                Kind: CellKind.Text,
                Capacity: capacity,
                Evicts: true
            )])
        )
    );
    private static string[] Lines(WorldIdentity identity) {
        Assert.True(condition: identity.TryReadState(
            name: "log",
            row: out var row
        ));

        return [.. (row.Cells ?? []).Select(selector: cell => cell.Value.AsText)];
    }

    [Fact]
    public void AnAppendPastCapacityDropsTheOldestLineAndNamesIt() {
        var identity = Identity(capacity: 2);

        foreach (var line in new[] { "first", "second" }) {
            Assert.True(condition: identity.TryAppendEvictingText(
                evictedKey: out var none,
                reason: out var reason,
                rowName: CellName.Parse(candidate: "log"),
                text: line
            ), userMessage: reason);
            Assert.Null(@object: none);
        }

        Assert.Equal(
            actual: Lines(identity: identity),
            expected: ["first", "second"]
        );
        Assert.True(condition: identity.TryAppendEvictingText(
            evictedKey: out var evicted,
            reason: out var overflowReason,
            rowName: CellName.Parse(candidate: "log"),
            text: "third"
        ), userMessage: overflowReason);
        Assert.Equal(
            actual: evicted?.Value,
            expected: "1"
        );
        Assert.Equal(
            actual: Lines(identity: identity),
            expected: ["second", "third"]
        );
    }
    [Fact]
    public void AnAppendToARowTheDocumentDoesNotDeclareIsRefusedByName() {
        var identity = Identity(capacity: 2);

        Assert.False(condition: identity.TryAppendEvictingText(
            evictedKey: out var evicted,
            reason: out var reason,
            rowName: CellName.Parse(candidate: "absent"),
            text: "line"
        ));
        Assert.Null(@object: evicted);
        Assert.Contains(
            expectedSubstring: "absent",
            actualString: reason
        );
    }
}
