using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: once its journal and change-walk buffers have grown to the largest scope it has
/// evaluated, a <see cref="StateArena"/> reads, writes, opens, commits, and rewinds a scalar cell without
/// allocating, on a slot row and on a keyed row alike, and a savepoint rewound inside an open scope hands its
/// vector snapshots back. A text write allocates its own payload and nothing else.</summary>
public sealed class ArenaAllocationLawTests {
    [Fact]
    public void AScalarReadAllocatesNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        for (var pass = 0; (pass < 64); pass++) {
            _ = arena.TryRead(
                key: slot,
                rowOrdinal: ArenaFixture.Score,
                value: out _
            );
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var pass = 0; (pass < 1024); pass++) {
            Assert.True(condition: arena.TryRead(
                key: slot,
                rowOrdinal: ArenaFixture.Score,
                value: out _
            ));
        }

        Assert.Equal(
            expected: 0L,
            actual: (GC.GetAllocatedBytesForCurrentThread() - before)
        );
    }
    [Fact]
    public void AScalarWriteScopeCommitAndRewindAllocateNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        AssertCyclesAllocateNothing(
            arena: arena,
            key: slot,
            rowOrdinal: ArenaFixture.Score
        );
    }
    [Fact]
    public void AKeyedRowWriteScopeCommitAndRewindAllocateNothing() {
        var (_, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryMint(
                key: out var gold,
                name: ArenaFixture.Name(value: "gold"),
                reason: out var reason,
                rowOrdinal: ArenaFixture.Purse,
                value: CellValue.Int(value: 1L)
            ),
            userMessage: reason
        );
        AssertCyclesAllocateNothing(
            arena: arena,
            key: gold,
            rowOrdinal: ArenaFixture.Purse
        );
    }
    [Fact]
    public void ASavepointRewoundInsideAScopeHandsBackItsVectorSnapshot() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var outer = arena.BeginScope();

        WriteVector(
            arena: arena,
            axis: 1,
            key: slot
        );

        var held = arena.Journal.ComponentLength;

        Assert.True(condition: (held > 0));

        for (var pass = 0; (pass < 512); pass++) {
            var savepoint = arena.BeginScope();

            WriteVector(
                arena: arena,
                axis: (pass % 8),
                key: slot
            );
            arena.Rewind(mark: savepoint);

            Assert.Equal(
                actual: arena.Journal.ComponentLength,
                expected: held
            );
        }

        arena.Commit(mark: outer);
    }

    private static void AssertCyclesAllocateNothing(StateArena arena, CellKey key, int rowOrdinal) {
        for (var pass = 0; (pass < 64); pass++) {
            Cycle(
                arena: arena,
                key: key,
                rowOrdinal: rowOrdinal,
                value: (pass % 2)
            );
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var pass = 0; (pass < 512); pass++) {
            Cycle(
                arena: arena,
                key: key,
                rowOrdinal: rowOrdinal,
                value: (pass % 2)
            );
        }

        Assert.Equal(
            expected: 0L,
            actual: (GC.GetAllocatedBytesForCurrentThread() - before)
        );
    }
    private static void Cycle(StateArena arena, CellKey key, int rowOrdinal, long value) {
        var committed = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: key,
            operand: value,
            reason: out _,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        ));

        arena.Commit(mark: committed);

        var rewound = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: key,
            operand: (value + 3L),
            reason: out _,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        ));

        arena.Rewind(mark: rewound);
    }
    private static void WriteVector(StateArena arena, CellKey key, int axis) => Assert.True(
        condition: arena.TryWriteVector(
            components: ArenaFixture.Unit(axis: axis).Components,
            key: key,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Embed
        ),
        userMessage: reason
    );
}
