using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a planned read of a declared cell answers exactly what the unplanned read answers,
/// whatever moved the row's members since the plan was made: a removal and an insert that hand the planned key's slot
/// to another member, inside an open scope or committed, and the rewind that hands it back. A plan never reads the
/// slot it cached once the row's key-to-slot map has moved.</summary>
public sealed class CellReadPlanLawTests {
    // The planned and the unplanned read agree, presence and value both.
    private static void Agrees(StateArena arena, CellKey key, ref CellReadPlan? plan) {
        var time = ArenaTime.Origin;
        var expected = arena.TryReadLiveNumber(
            key: key,
            rowOrdinal: ArenaFixture.Purse,
            time: in time,
            value: out var expectedValue
        );

        Assert.Equal(
            actual: arena.TryReadPlannedNumber(
                key: key,
                plan: ref plan,
                rowOrdinal: ArenaFixture.Purse,
                time: in time,
                value: out var actual
            ),
            expected: expected
        );
        Assert.Equal(
            actual: actual,
            expected: expectedValue
        );
    }
    // Removes the planned member and inserts another, which takes the slot the removed member left.
    private static void HandOver(StateArena arena, CellKey planned, int plannedSlot) {
        Assert.True(condition: arena.TryRemove(
            key: planned,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Purse
        ), userMessage: reason);
        Assert.True(condition: arena.TryInsert(
            key: out var other,
            name: ArenaFixture.Name(value: "c"),
            position: -1,
            reason: out reason,
            rowOrdinal: ArenaFixture.Purse,
            value: CellValue.Int(value: 9L)
        ), userMessage: reason);
        Assert.True(condition: arena.TryCellSlot(
            key: other,
            rowOrdinal: ArenaFixture.Purse,
            slot: out var taken
        ));
        Assert.Equal(
            actual: taken,
            expected: plannedSlot
        );
    }

    [Fact]
    public void APlanNeverReadsTheSlotAnotherMemberTookAfterTheRowsMembersMoved() {
        var (catalog, arena) = ArenaFixture.Build();
        var planned = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );

        Assert.True(condition: arena.TryInsert(
            key: out var inserted,
            name: ArenaFixture.Name(value: "a"),
            position: -1,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Purse,
            value: CellValue.Int(value: 3L)
        ), userMessage: reason);
        Assert.Equal(
            actual: inserted,
            expected: planned
        );
        Assert.True(condition: arena.TryCellSlot(
            key: planned,
            rowOrdinal: ArenaFixture.Purse,
            slot: out var plannedSlot
        ));

        CellReadPlan? plan = null;

        Agrees(arena: arena, key: planned, plan: ref plan);
        Assert.NotNull(@object: plan);

        var mark = arena.BeginScope();

        HandOver(arena: arena, planned: planned, plannedSlot: plannedSlot);
        Agrees(arena: arena, key: planned, plan: ref plan);
        arena.Rewind(mark: mark);
        Agrees(arena: arena, key: planned, plan: ref plan);

        mark = arena.BeginScope();
        HandOver(arena: arena, planned: planned, plannedSlot: plannedSlot);
        arena.Commit(mark: mark);
        Agrees(arena: arena, key: planned, plan: ref plan);
    }
}
