using Puck.Assets.Documents;
using Puck.Abstractions.Counting;
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

        Assert.Equal(actual: allocated, expected: 0L);
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
        var plan = new ArenaUndoPlan(Depth: depth, Name: "turn", Rows: rows);
        var warm = new StateArena(catalog, section, ArenaTime.Origin);

        warm.ConfigureUndo(plans: [plan]);

        // Configuring undo is a first-time act, so every window configures an arena of its own.
        var arenas = Enumerable.Range(count: AllocationWindow.MaximumWindows, start: 0).Select(selector: _ => new StateArena(catalog, section, ArenaTime.Origin)).ToArray();
        var reservation = StateArena.EstimateUndoBytes(catalog: catalog, layout: arenas[0].Layout, plans: [plan]);
        var next = 0;
        var allocated = AllocationWindow.Measure(window: () => arenas[next++].ConfigureUndo(plans: [plan]));

        Assert.InRange(actual: allocated, high: reservation, low: 1L);
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
        var plan = new ArenaUndoPlan(Depth: 32, Name: "turn", Rows: rows);

        Assert.InRange(StateArena.EstimateUndoBytes(catalog: catalog, layout: arena.Layout, plans: [plan]), 1L, ArenaCapacity.MaxJournalBytes);
        arena.ConfigureUndo(plans: [plan]);
        var hashes = new ulong[33];

        hashes[0] = arena.ComputeHash();
        var handles = new StateInstanceHandle[256];

        for (var turn = 1; (turn <= 32); turn++) {
            arena.BeginUndoTurn(group: "turn");
            arena.BeginUndoPass(group: "turn");
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
            arena.EndUndoPass(group: "turn");
            arena.CommitUndoTurn(group: "turn");
            hashes[turn] = arena.ComputeHash();
        }
        for (var turn = 32; (turn > 0); turn--) {
            Assert.True(condition: arena.TryRewindGroup(group: "turn", reason: out var reason), userMessage: reason);
            Assert.Equal(hashes[(turn - 1)], arena.ComputeHash());
        }
    }
    [Fact]
    public void SnapshotWithNullNestedPayloadIsRefusedInsteadOfThrowing() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(Entries: [null!], Rewindable: true)], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "null journal entry");
    }
    [Fact]
    public void SnapshotCannotPutTextStorageOnANumericRow() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        var slot = arena.Layout[ArenaFixture.Score].CellStart;
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: ArenaColumn.Text, Components: null, Index: slot, MemberKey: null, Number: 0L, Observation: null, Text: "forged", Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot store");
    }
    [Fact]
    public void SnapshotRejectsANoncanonicalReferenceNumber() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Label])]);
        var slot = arena.Layout[ArenaFixture.Label].CellStart;
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Label], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: ArenaColumn.Text, Components: null, Index: slot, MemberKey: null, Number: 1L, Observation: null, Text: "forged", Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "noncanonical number");
    }
    [InlineData(ArenaColumn.Provenance)]
    [InlineData(ArenaColumn.Visibility)]
    [InlineData(ArenaColumn.Behavior)]
    [InlineData(ArenaColumn.Observation)]
    [Theory]
    public void SnapshotCannotRetainAColumnNoRuleWrites(ArenaColumn column) {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        var slot = arena.Layout[ArenaFixture.Score].CellStart;
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Score], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: column, Components: null, Index: slot, MemberKey: null, Number: 0L, Observation: null, Text: null, Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot store");
    }
    [Fact]
    public void ABoardRetainsThirtyTwoTurnsWithinTheCeilingAndAnUnretainedColumnWriteRefusesTheRewind() {
        var section = new StateSection(
            Lattices: [new LatticeTopology.Grid(Name: "board", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 16, Depth: 16)],
            Rows: [new StateRow(Name: ArenaFixture.Name(value: "board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "board"))]
        );
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var plan = new ArenaUndoPlan(Depth: 32, Name: "turn", Rows: [0]);
        var cell = arena.Keys.Intern(name: ArenaFixture.Name(value: "17"));

        Assert.InRange(StateArena.EstimateUndoBytes(catalog: catalog, layout: arena.Layout, plans: [plan]), 1L, (ArenaCapacity.MaxJournalBytes / 4));
        arena.ConfigureUndo(plans: [plan]);
        var before = arena.ComputeHash();

        arena.BeginUndoTurn(group: "turn");
        arena.BeginUndoPass(group: "turn");
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(key: cell, operand: 5L, reason: out var reason, rowOrdinal: 0, write: StateWriteKind.Set), userMessage: reason);
        arena.Commit(mark: mark);
        arena.EndUndoPass(group: "turn");
        arena.CommitUndoTurn(group: "turn");
        Assert.True(condition: arena.TryRewindGroup(group: "turn", reason: out reason), userMessage: reason);
        Assert.Equal(before, arena.ComputeHash());

        arena.BeginUndoTurn(group: "turn");
        arena.BeginUndoPass(group: "turn");
        mark = arena.BeginScope();
        Assert.True(condition: arena.TryWrite(key: cell, operand: 5L, reason: out reason, rowOrdinal: 0, write: StateWriteKind.Set), userMessage: reason);
        Assert.True(condition: arena.TryWriteBehavior(behavior: StateCellBehavior.None, key: cell, rowOrdinal: 0));
        arena.Commit(mark: mark);
        arena.EndUndoPass(group: "turn");
        arena.CommitUndoTurn(group: "turn");
        Assert.False(condition: arena.TryRewindGroup(group: "turn", reason: out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "outside its declared rows");
    }
    // A turn whose commits clear a row and rebuild it to the same bytes wrote positions and changed nothing: it takes
    // no retained slot, so the next rewind still reaches the turn before it.
    [Fact]
    public void ATurnThatChangesNothingTakesNoSlotAndLeavesTheTurnBeforeItRewindable() {
        var (catalog, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 4, Name: "play", Rows: [ArenaFixture.Score])]);
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var before = arena.ComputeHash();

        void Turn(params long[] writes) {
            arena.BeginUndoTurn(group: "play");
            arena.BeginUndoPass(group: "play");
            foreach (var value in writes) {
                var mark = arena.BeginScope();

                Assert.True(condition: arena.TryWrite(key: slot, operand: value, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
                arena.Commit(mark: mark);
            }
            arena.EndUndoPass(group: "play");
            arena.CommitUndoTurn(group: "play");
        }

        Turn(9L);
        var afterMove = arena.ComputeHash();

        Turn(0L, 9L);
        Assert.Equal(expected: afterMove, actual: arena.ComputeHash());
        Assert.Single(collection: Assert.Single(collection: arena.ExportUndoSnapshot().Groups).Segments);
        Assert.True(condition: arena.TryRewindGroup(group: "play", reason: out var rewound), userMessage: rewound);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
    }
    [Fact]
    public void EmptyRowPlanCannotBypassRetainedStorageBudget() {
        var (_, arena) = ArenaFixture.Build();

        Assert.Throws<ArgumentException>(testCode: () => arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: int.MaxValue, Name: "play", Rows: [])]));
        Assert.Empty(collection: arena.ExportUndoSnapshot().Groups);
    }
    [Fact]
    public void SnapshotWithInconsistentMemberCountIsRefused() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Tokens])]);
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Tokens], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: ArenaColumn.MemberCount, Components: null, Index: ArenaFixture.Tokens, MemberKey: null, Number: 1L, Observation: null, Text: null, Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "member count");
    }
    [Fact]
    public void SnapshotWithDuplicateMemberKeyIsRefused() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Tokens])]);
        var secondSlot = (arena.Layout[ArenaFixture.Tokens].CellStart + 1);
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, [ArenaFixture.Tokens], [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: ArenaColumn.MemberKey, Components: null, Index: secondSlot, MemberKey: "a", Number: 0L, Observation: null, Text: null, Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "duplicate member");
    }
    [Fact]
    public void SnapshotWithPoolLifetimeDifferentFromGenerationIsRefused() {
        var record = new StateRecord(Name: ArenaFixture.Name(value: "piece"), Fields: [new StatePoolField(Name: ArenaFixture.Name(value: "score"))]);
        var section = new StateSection(Records: [record], Pools: [new StatePool(Name: ArenaFixture.Name(value: "pieces"), Record: record.Name, Capacity: 2, Initial: [new StatePoolSeed(Slot: 0)])]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var pool = catalog.Pools[0];
        var rows = pool.Fields.Select(selector: field => field.RowOrdinal).Append(element: pool.DomainRowOrdinal).Append(element: pool.GenerationRowOrdinal).Order().ToArray();

        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: rows)]);
        var domainSlot = arena.Layout[pool.DomainRowOrdinal].CellStart;
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot("play", 2, rows, [new ArenaUndoSegmentSnapshot(true, [new ArenaUndoEntrySnapshot(Column: ArenaColumn.Number, Components: null, Index: domainSlot, MemberKey: null, Number: 1L, Observation: null, Text: null, Visibility: null)])], null)]);

        Assert.False(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "different from its generation row");
    }
    [Fact]
    public void WarmedRetainedTurnAndRewindAllocateNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        void Cycle() {
            arena.BeginUndoTurn(group: "play");
            arena.BeginUndoPass(group: "play");
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryWrite(key: slot, operand: 9L, reason: out _, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set));
            arena.Commit(mark: mark);
            arena.EndUndoPass(group: "play");
            arena.CommitUndoTurn(group: "play");
            Assert.True(condition: arena.TryRewindGroup(group: "play", reason: out _));
        }

        for (var pass = 0; (pass < 64); pass++) {
            Cycle();
        }
        var allocated = AllocationWindow.Least(window: () => {
            for (var pass = 0; (pass < 512); pass++) {
                Cycle();
            }
        });

        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void EightTurnsRewindToEveryRecordedHash() {
        var (catalog, arena) = ArenaFixture.Build();
        var plan = new ArenaUndoPlan(Depth: 8, Name: "play", Rows: [ArenaFixture.Score]);

        arena.ConfigureUndo(plans: [plan]);
        var hashes = new ulong[9];

        hashes[0] = arena.ComputeHash();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        for (var turn = 1; (turn <= 8); turn++) {
            arena.BeginUndoTurn(group: "play");
            arena.BeginUndoPass(group: "play");
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryWrite(key: slot, operand: turn, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
            arena.Commit(mark: mark);
            arena.EndUndoPass(group: "play");
            arena.CommitUndoTurn(group: "play");
            hashes[turn] = arena.ComputeHash();
        }

        for (var turn = 8; (turn > 0); turn--) {
            Assert.True(condition: arena.TryRewindGroup(group: "play", reason: out var reason), userMessage: reason);
            Assert.Equal(hashes[(turn - 1)], arena.ComputeHash());
        }
    }
    [Fact]
    public void PendingTurnRoundTripsThroughTypedSnapshot() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        arena.BeginUndoTurn(group: "play");
        var snapshot = arena.ExportUndoSnapshot();

        Assert.True(condition: arena.ValidateUndoSnapshot(reason: out var reason, snapshot: snapshot), userMessage: reason);
        Assert.True(condition: arena.TryImportUndoSnapshot(reason: out reason, snapshot: snapshot), userMessage: reason);
        Assert.True(condition: arena.UndoTurnPending(group: "play"));
    }
    [Fact]
    public void SuccessfulRelayoutDropsRetainedHistory() {
        var (_, arena) = ArenaFixture.Build();
        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Depth: 2, Name: "play", Rows: [ArenaFixture.Score])]);
        arena.BeginUndoTurn(group: "play");
        arena.CommitUndoTurn(group: "play");
        var section = ArenaFixture.Section();

        Assert.True(condition: arena.TryRelayout(StateCatalog.Compile(section: section), section, ArenaTime.Origin, out var reason), userMessage: reason);
        Assert.Empty(collection: arena.ExportUndoSnapshot().Groups);
    }
}
