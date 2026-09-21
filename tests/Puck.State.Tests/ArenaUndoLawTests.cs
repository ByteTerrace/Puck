using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: retained undo turns span ordinary scopes without leaving one open, restore in
/// newest-first order, participate in the arena hash and checkpoint, and are cleared by relayout.</summary>
public sealed class ArenaUndoLawTests {
    [Fact]
    public void AnArenaWithoutUndoReservesNothingAndHashesWithoutAllocation() {
        var (catalog, arena) = ArenaFixture.Build();
        var before = arena.ComputeHash();

        arena.ConfigureUndo(plans: null);

        Assert.Equal(0L, StateArena.EstimateUndoBytes(catalog: catalog, layout: arena.Layout, plans: []));
        Assert.Equal(before, arena.ComputeHash());
        var allocated = AllocationWindow.Least(window: () => {
            for (var sample = 0; (sample < 512); sample++) { arena.ConfigureUndo(plans: null); }
        });

        Assert.Equal(0L, allocated);
        Assert.Equal(before, arena.ComputeHash());
    }
    [InlineData(1)]
    [InlineData(32)]
    [Theory]
    public void UndoReservationCoversConfiguredStorage(int depth) {
        var record = new StateRecord(Name: ArenaFixture.Name(value: "piece"), Fields: [
            new StatePoolField(Name: ArenaFixture.Name(value: "cell")),
            new StatePoolField(Name: ArenaFixture.Name(value: "noun")),
            new StatePoolField(Name: ArenaFixture.Name(value: "properties")),
        ]);
        var section = new StateSection(Records: [record], Pools: [new StatePool(Name: ArenaFixture.Name(value: "pieces"), Record: record.Name, Capacity: 256)]);
        var catalog = StateCatalog.Compile(section: section);
        var pool = catalog.Pools[0];
        var rows = pool.Fields.Select(selector: field => field.RowOrdinal).Append(element: pool.DomainRowOrdinal).Append(element: pool.GenerationRowOrdinal).Order().ToArray();
        var plan = new ArenaUndoPlan("turn", rows, depth);
        var warm = new StateArena(catalog, section, ArenaTime.Origin);

        warm.ConfigureUndo([plan]);

        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var reservation = StateArena.EstimateUndoBytes(catalog, arena.Layout, [plan]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        arena.ConfigureUndo([plan]);

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.InRange(actual: allocated, low: 1L, high: reservation);
    }
    [Fact]
    public void ADepthThirtyTwoPoolOfUsefulCapacityFitsTheRetainedCeiling() {
        var record = new StateRecord(Name: ArenaFixture.Name(value: "piece"), Fields: [
            new StatePoolField(Name: ArenaFixture.Name(value: "cell")),
            new StatePoolField(Name: ArenaFixture.Name(value: "noun")),
            new StatePoolField(Name: ArenaFixture.Name(value: "properties")),
        ]);
        var section = new StateSection(Records: [record], Pools: [new StatePool(Name: ArenaFixture.Name(value: "pieces"), Record: record.Name, Capacity: 256,
            Initial: Enumerable.Range(count: 256, start: 0).Select(selector: slot => new StatePoolSeed(slot)).ToArray())]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var pool = catalog.Pools[0];
        var rows = pool.Fields.Select(selector: field => field.RowOrdinal).Append(element: pool.DomainRowOrdinal).Append(element: pool.GenerationRowOrdinal).Order().ToArray();
        var plan = new ArenaUndoPlan("turn", rows, 32);

        Assert.InRange(StateArena.EstimateUndoBytes(catalog, arena.Layout, [plan]), 1L, ArenaCapacity.MaxJournalBytes);
        arena.ConfigureUndo([plan]);
        var hashes = new ulong[33];

        hashes[0] = arena.ComputeHash();
        var handles = new StateInstanceHandle[256];

        for (var turn = 1; (turn <= 32); turn++) {
            arena.BeginUndoTurn("turn");
            arena.BeginUndoPass("turn");
            var mark = arena.BeginScope();

            Assert.Equal(256, arena.CopyPoolSnapshot(pool.Ordinal, handles));
            Assert.True(condition: arena.TryRelease(handle: handles[turn], reason: out var reason), userMessage: reason);
            Assert.True(condition: arena.TryClaim(pool.Ordinal, out handles[turn], out reason), userMessage: reason);
            foreach (var handle in handles) {
                for (var field = 0; (field < 3); field++) {
                    Assert.True(condition: arena.TryWrite(handle, field, CellValue.Int(value: (turn + field)), out reason), userMessage: reason);
                }
            }
            arena.Commit(mark: mark);
            arena.EndUndoPass("turn");
            arena.CommitUndoTurn("turn");
            hashes[turn] = arena.ComputeHash();
        }
        for (var turn = 32; (turn > 0); turn--) {
            Assert.True(arena.TryRewindTurn("turn", out var reason), reason);
            Assert.Equal(hashes[(turn - 1)], arena.ComputeHash());
        }
    }
    [Fact]
    public void SnapshotWithNullNestedPayloadIsRefusedInsteadOfThrowing() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(true, [null!])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("null journal entry", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotCannotPutTextStorageOnANumericRow() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        var slot = arena.Layout[ArenaFixture.Score].CellStart;
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(ArenaColumn.Text, slot, 0L, "forged", null, null, null, null)])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("cannot store", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotRejectsANoncanonicalReferenceNumber() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        var slot = arena.Layout[ArenaFixture.Score].CellStart;
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(ArenaColumn.Provenance, slot, 1L, "issuer", null, null, null, null)])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("noncanonical number", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void EmptyRowPlanCannotBypassRetainedStorageBudget() {
        var (_, arena) = ArenaFixture.Build();

        Assert.Throws<ArgumentException>(() => arena.ConfigureUndo([new ArenaUndoPlan("play", [], int.MaxValue)]));
        Assert.Empty(arena.ExportUndoSnapshot().Groups);
    }
    [Fact]
    public void SnapshotWithInconsistentMemberCountIsRefused() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Tokens], 2)]);
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Tokens], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(ArenaColumn.MemberCount, ArenaFixture.Tokens, 1L, null, null, null, null, null)])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("member count", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotWithDuplicateMemberKeyIsRefused() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Tokens], 2)]);
        var secondSlot = (arena.Layout[ArenaFixture.Tokens].CellStart + 1);
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Tokens], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(ArenaColumn.MemberKey, secondSlot, 0L, null, null, null, "a", null)])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("duplicate member", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void SnapshotWithPoolLifetimeDifferentFromGenerationIsRefused() {
        var record = new StateRecord(Name: ArenaFixture.Name(value: "piece"), Fields: [new StatePoolField(Name: ArenaFixture.Name(value: "score"))]);
        var section = new StateSection(Records: [record], Pools: [new StatePool(Name: ArenaFixture.Name(value: "pieces"), Record: record.Name, Capacity: 2, Initial: [new StatePoolSeed(Slot: 0)])]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var pool = catalog.Pools[0];
        var rows = pool.Fields.Select(selector: field => field.RowOrdinal).Append(element: pool.DomainRowOrdinal).Append(element: pool.GenerationRowOrdinal).Order().ToArray();

        arena.ConfigureUndo([new ArenaUndoPlan("play", rows, 2)]);
        var domainSlot = arena.Layout[pool.DomainRowOrdinal].CellStart;
        var snapshot = new ArenaUndoSnapshot([new ArenaUndoGroupSnapshot("play", 2, rows, [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(ArenaColumn.Number, domainSlot, 1L, null, null, null, null, null)])], null)]);

        Assert.False(arena.ValidateUndoSnapshot(snapshot, out var reason));
        Assert.Contains("different from its generation row", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void WarmedRetainedTurnAndRewindAllocateNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        void Cycle() {
            arena.BeginUndoTurn("play");
            arena.BeginUndoPass("play");
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryWrite(key: slot, operand: 9L, reason: out _, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set));
            arena.Commit(mark: mark);
            arena.EndUndoPass("play");
            arena.CommitUndoTurn("play");
            Assert.True(arena.TryRewindTurn("play", out _));
        }

        for (var pass = 0; (pass < 64); pass++) {
            Cycle();
        }
        var allocated = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                Cycle();
            }
        });

        Assert.Equal(0L, allocated);
    }
    [Fact]
    public void EightTurnsRewindToEveryRecordedHash() {
        var (catalog, arena) = ArenaFixture.Build();
        var plan = new ArenaUndoPlan("play", [ArenaFixture.Score], 8);

        arena.ConfigureUndo([plan]);
        var hashes = new ulong[9];

        hashes[0] = arena.ComputeHash();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        for (var turn = 1; (turn <= 8); turn++) {
            arena.BeginUndoTurn("play");
            arena.BeginUndoPass("play");
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryWrite(key: slot, operand: turn, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
            arena.Commit(mark: mark);
            arena.EndUndoPass("play");
            arena.CommitUndoTurn("play");
            hashes[turn] = arena.ComputeHash();
        }

        for (var turn = 8; (turn > 0); turn--) {
            Assert.True(arena.TryRewindTurn("play", out var reason), reason);
            Assert.Equal(hashes[(turn - 1)], arena.ComputeHash());
        }
    }
    [Fact]
    public void PendingTurnRoundTripsThroughTypedSnapshot() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        arena.BeginUndoTurn("play");
        var snapshot = arena.ExportUndoSnapshot();

        Assert.True(arena.ValidateUndoSnapshot(snapshot, out var reason), reason);
        Assert.True(arena.TryImportUndoSnapshot(snapshot, out reason), reason);
        Assert.True(arena.UndoTurnPending("play"));
    }
    [Fact]
    public void SuccessfulRelayoutDropsRetainedHistory() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo([new ArenaUndoPlan("play", [ArenaFixture.Score], 2)]);
        arena.BeginUndoTurn("play");
        arena.CommitUndoTurn("play");
        var section = ArenaFixture.Section();

        Assert.True(condition: arena.TryRelayout(StateCatalog.Compile(section: section), section, ArenaTime.Origin, out var reason), userMessage: reason);
        Assert.Empty(arena.ExportUndoSnapshot().Groups);
    }
}
