using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: once its journal and change-walk buffers have grown to the largest scope it has
/// evaluated, a <see cref="StateArena"/> reads, writes, opens, commits, and rewinds a scalar cell without
/// allocating, on a slot row and on a keyed row alike, and a savepoint rewound inside an open scope hands its
/// vector snapshots back. A text write allocates its own payload and nothing else.</summary>
public sealed class ArenaAllocationLawTests {
    [Fact]
    public void WarmedPoolHandleReadsAndSnapshotCopiesAllocateNothing() {
        var section = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "r"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "value"))])],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "items"), Record: CellName.Parse(candidate: "r"), Capacity: 256)]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
        var handles = new StateInstanceHandle[256];

        for (var slot = 0; (slot < handles.Length); slot++) {
            Assert.True(condition: arena.TryClaim(handle: out handles[slot], poolOrdinal: 0, reason: out _));
        }
        for (var pass = 0; (pass < 64); pass++) {
            _ = arena.CopyPoolSnapshot(destination: handles, poolOrdinal: 0);
            _ = arena.TryRead(handle: handles[127], fieldOrdinal: 0, value: out _);
        }
        var allRead = true;

        var before = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                allRead &= (handles.Length == arena.CopyPoolSnapshot(destination: handles, poolOrdinal: 0));
                allRead &= arena.TryRead(handle: handles[127], fieldOrdinal: 0, value: out _);
            }
        });

        var allocated = before;

        Assert.True(condition: allRead);
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void WarmedPoolClaimWriteReleaseAndRewindAllocateNothing() {
        var section = new StateSection(
            Records: [new StateRecord(Name: CellName.Parse(candidate: "r"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "value"))])],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "items"), Record: CellName.Parse(candidate: "r"), Capacity: 2)]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

        void Cycle() {
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out _));
            Assert.True(condition: arena.TryWrite(handle: handle, fieldOrdinal: 0, value: CellValue.Int(value: 5), reason: out _));
            Assert.True(condition: arena.TryRelease(handle: handle, reason: out _));
            arena.Rewind(mark: mark);
        }

        for (var pass = 0; (pass < 64); pass++) {
            Cycle();
        }
        var before = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                Cycle();
            }
        });

        Assert.Equal(expected: 0L, actual: before);
    }
    [Fact]
    public void WarmedPairCascadeAndRewindAllocateNothing() {
        var record = new StateRecord(Name: CellName.Parse(candidate: "r"));
        var section = new StateSection(
            Records: [record],
            Pools: [new StatePool(Name: CellName.Parse(candidate: "nodes"), Record: record.Name, Capacity: 2, Initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 1)])],
            PairPools: [new StatePairPool(Name: CellName.Parse(candidate: "edges"), Record: record.Name, LeftPool: CellName.Parse(candidate: "nodes"), RightPool: CellName.Parse(candidate: "nodes"), MaxLive: 2)]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
        var endpoints = arena.SnapshotPool(poolOrdinal: 0);

        void Cycle() {
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryClaimPair(poolOrdinal: 1, leftHandle: endpoints[0], rightHandle: endpoints[1], handle: out var pair, reason: out _));
            Assert.True(condition: arena.TryResolve(handle: pair, position: out _));
            Assert.True(condition: arena.TryRelease(handle: endpoints[0], reason: out _));
            Assert.False(condition: arena.TryResolve(handle: pair, position: out _));
            arena.Rewind(mark: mark);
        }

        for (var pass = 0; (pass < 64); pass++) {
            Cycle();
        }
        var before = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                Cycle();
            }
        });

        Assert.Equal(expected: 0L, actual: before);
    }
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

        var before = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 1024); pass++) {
                Assert.True(condition: arena.TryRead(
                    key: slot,
                    rowOrdinal: ArenaFixture.Score,
                    value: out _
                ));
            }
        });

        Assert.Equal(
            expected: 0L,
            actual: before
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

        var before = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                Cycle(
                    arena: arena,
                    key: key,
                    rowOrdinal: rowOrdinal,
                    value: (pass % 2)
                );
            }
        });

        Assert.Equal(
            expected: 0L,
            actual: before
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
