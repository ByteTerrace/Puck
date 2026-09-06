using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="WorldDeadlineTable{TToken}"/> stays sorted ascending by due tick regardless of add order,
/// dequeues an entry only once its due tick has arrived (never early, and never holds one back once it has),
/// preserves insertion order among entries that share a due tick, and <see cref="WorldDeadlineTable{TToken}.Remove"/>
/// cancels a specific entry ahead of its own deadline without disturbing the rest of the table.
/// </summary>
public sealed class WorldDeadlineTableLawTests {
    [Fact]
    public void EmptyTableDequeuesNothing() {
        var table = new WorldDeadlineTable<int>();

        Assert.False(table.TryDequeueDue(tick: long.MaxValue, token: out _));
        Assert.Equal(expected: 0, actual: table.Count);
    }

    [Fact]
    public void DequeueHoldsAnEntryUntilItsExactDueTickArrives() {
        var table = new WorldDeadlineTable<string>();
        table.Add(dueTick: 10, token: "a");

        Assert.False(table.TryDequeueDue(tick: 9, token: out _));
        Assert.True(table.TryDequeueDue(tick: 10, token: out var token));
        Assert.Equal(expected: "a", actual: token);
        Assert.Equal(expected: 0, actual: table.Count);
    }

    [Fact]
    public void OutOfOrderAddsDequeueInAscendingDueTickOrder() {
        var table = new WorldDeadlineTable<int>();
        table.Add(dueTick: 30, token: 3);
        table.Add(dueTick: 10, token: 1);
        table.Add(dueTick: 20, token: 2);

        Assert.True(table.TryDequeueDue(tick: 100, token: out var first));
        Assert.True(table.TryDequeueDue(tick: 100, token: out var second));
        Assert.True(table.TryDequeueDue(tick: 100, token: out var third));
        Assert.False(table.TryDequeueDue(tick: 100, token: out _));
        Assert.Equal([1, 2, 3], [first, second, third]);
    }

    [Fact]
    public void EntriesSharingADueTickDequeueInInsertionOrder() {
        var table = new WorldDeadlineTable<int>();
        table.Add(dueTick: 5, token: 1);
        table.Add(dueTick: 5, token: 2);
        table.Add(dueTick: 5, token: 3);

        Assert.True(table.TryDequeueDue(tick: 5, token: out var first));
        Assert.True(table.TryDequeueDue(tick: 5, token: out var second));
        Assert.True(table.TryDequeueDue(tick: 5, token: out var third));
        Assert.Equal([1, 2, 3], [first, second, third]);
    }

    [Fact]
    public void SweepOnlyDrainsWhatIsDueLeavingLaterEntriesInPlace() {
        var table = new WorldDeadlineTable<int>();
        table.Add(dueTick: 10, token: 1);
        table.Add(dueTick: 20, token: 2);
        table.Add(dueTick: 30, token: 3);

        Assert.True(table.TryDequeueDue(tick: 20, token: out var due));
        Assert.Equal(expected: 1, actual: due);
        Assert.True(table.TryDequeueDue(tick: 20, token: out due));
        Assert.Equal(expected: 2, actual: due);
        Assert.False(table.TryDequeueDue(tick: 20, token: out _));
        Assert.Equal(expected: 1, actual: table.Count);

        Assert.True(table.TryDequeueDue(tick: 30, token: out due));
        Assert.Equal(expected: 3, actual: due);
    }

    [Fact]
    public void RemoveCancelsOneEntryAheadOfItsDeadlineWithoutDisturbingTheRest() {
        var table = new WorldDeadlineTable<int>();
        table.Add(dueTick: 10, token: 1);
        table.Add(dueTick: 20, token: 2);
        table.Add(dueTick: 30, token: 3);

        Assert.True(table.Remove(token: 2));
        Assert.False(table.Remove(token: 2));
        Assert.Equal(expected: 2, actual: table.Count);

        Assert.True(table.TryDequeueDue(tick: 100, token: out var first));
        Assert.True(table.TryDequeueDue(tick: 100, token: out var second));
        Assert.Equal([1, 3], [first, second]);
    }

    [Fact]
    public void ClearDropsEveryEntry() {
        var table = new WorldDeadlineTable<int>();
        table.Add(dueTick: 1, token: 1);
        table.Add(dueTick: 2, token: 2);

        table.Clear();

        Assert.Equal(expected: 0, actual: table.Count);
        Assert.False(table.TryDequeueDue(tick: long.MaxValue, token: out _));
    }
}
