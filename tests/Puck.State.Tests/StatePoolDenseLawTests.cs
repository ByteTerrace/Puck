using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>Fixed pool slots preserve sparse identities, bounded journal work, and retained continuation.</summary>
public sealed class StatePoolDenseLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section(int capacity, IReadOnlyList<StatePoolSeed>? initial = null) => new(
        Records: [new StateRecord(Name: Name(value: "item"), Fields: [new StatePoolField(Name: Name(value: "value"), Default: CellValue.Int(value: 7))])],
        Pools: [new StatePool(Name: Name(value: "items"), Record: Name(value: "item"), Capacity: capacity, Initial: initial)]
    );
    private static StateArena Build(StateSection section) => new(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

    [Fact]
    public void ClaimDefaultsAreAdmittedAtCompilationIncludingBooleanEnvelopes() {
        var section = Section(capacity: 4) with {
            Records = [new StateRecord(Name: Name(value: "item"), Fields: [
            new StatePoolField(Name: Name(value: "ready"), Kind: CellKind.Bool, Default: CellValue.Bool(value: false), Min: 1),
        ])],
        };
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));

        Assert.Contains(expectedSubstring: "outside its declared", actualString: refusal.Message);
    }
    [Fact]
    public void MutationJournalWidthDoesNotGrowWithLiveCount() {
        long? expected = null;

        foreach (var capacity in new[] { 4, 256, 4096 }) {
            var middle = (capacity / 2);
            var section = Section(capacity: capacity, initial: Enumerable.Range(count: capacity, start: 0).Where(predicate: slot => (slot != middle)).Select(selector: slot => new StatePoolSeed(Slot: slot)).ToArray());
            var arena = Build(section: section);
            var before = arena.ComputeHash();
            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason), userMessage: reason);
            Assert.Equal(expected: middle, actual: handle.Slot);
            Assert.True(condition: arena.TryRelease(handle: handle, reason: out reason), userMessage: reason);
            expected ??= arena.Journal.Bytes;
            Assert.Equal(expected: expected.Value, actual: arena.Journal.Bytes);
            arena.Rewind(mark: mark);
            Assert.Equal(expected: before, actual: arena.ComputeHash());

            // Scope buffers have warmed. Candidate claim, read, release and rewind allocate nothing.
            var valid = true;
            var allocated = AllocationWindow.Least(window: () => {
                for (var iteration = 0; (iteration < 128); iteration++) {
                    var candidate = arena.BeginScope();

                    valid &= arena.TryClaim(handle: out var claimed, poolOrdinal: 0, reason: out _);
                    valid &= (arena.TryReadLive(handle: claimed, fieldOrdinal: 0, time: ArenaTime.Origin, value: out var value) && (value.AsInt == 7));
                    valid &= arena.TryRelease(handle: claimed, reason: out _);
                    arena.Rewind(mark: candidate);
                }
            });

            Assert.True(condition: valid);
            Assert.Equal(actual: allocated, expected: 0L);
            Assert.Equal(expected: before, actual: arena.ComputeHash());
        }
    }
    [Fact]
    public void SparseRowsAddressIdentitySlotsAndWordsSkipHoles() {
        var section = Section(capacity: 130, initial: [new StatePoolSeed(Slot: 64), new StatePoolSeed(Slot: 129)]);
        var arena = Build(section: section);
        var pool = arena.Catalog.Pools[0];
        var field = pool.Fields[0].RowOrdinal;
        var handles = arena.SnapshotPool(poolOrdinal: 0);

        Assert.Equal(expected: new[] { 64, 129 }, actual: handles.Select(selector: handle => handle.Slot));
        Assert.Equal(expected: 2, actual: arena.CellCount(rowOrdinal: field));
        var cursor = 0;

        Assert.True(condition: arena.TryNextCell(rowOrdinal: field, cursor: ref cursor, key: out var first));
        Assert.Equal(expected: "64", actual: arena.Keys[first].Value);
        Assert.True(condition: arena.TryNextCell(rowOrdinal: field, cursor: ref cursor, key: out var second));
        Assert.Equal(expected: "129", actual: arena.Keys[second].Value);
        Assert.False(condition: arena.TryNextCell(rowOrdinal: field, cursor: ref cursor, key: out var end));
        Assert.Equal(expected: default, actual: end);
        Assert.True(condition: arena.TryWrite(handle: handles[1], fieldOrdinal: 0, value: CellValue.Int(value: 23), reason: out _));
        Assert.True(condition: arena.TryResolve(handle: handles[1], position: out var position));
        Assert.Equal(actual: position, expected: 129);
        Span<long> word = stackalloc long[2];

        _ = arena.ReadWord(rowOrdinal: field, word: word);
        Assert.Equal(expected: new long[] { 7, 23 }, actual: word.ToArray());
        var stored = section with { Pools = arena.ToPools() };
        var restored = Build(section: stored);

        Assert.Equal(expected: arena.ComputeHash(), actual: restored.ComputeHash());
        Assert.True(condition: arena.TryRelayout(catalog: StateCatalog.Compile(section: stored), section: stored, time: ArenaTime.Origin, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: restored.ComputeHash(), actual: arena.ComputeHash());
        Assert.True(condition: arena.TryResolvePoolSlot(poolOrdinal: 0, slot: 129, handle: out var retained));
        Assert.True(condition: arena.TryRead(handle: retained, fieldOrdinal: 0, value: out var read));
        Assert.Equal(expected: 23L, actual: read.AsInt);
    }
    [Fact]
    public void SparsePairCascadeAndRetainedUndoSurviveReload() {
        var section = Section(capacity: 17, initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 16)]) with {
            PairPools = [new StatePairPool(Name: Name(value: "edges"), Record: Name(value: "item"), LeftPool: Name(value: "items"), RightPool: Name(value: "items"), MaxLive: 2, Directed: true, AllowSelf: true)],
        };
        var arena = Build(section: section);
        var nodes = arena.SnapshotPool(poolOrdinal: 0);

        Assert.True(condition: arena.TryClaimPair(poolOrdinal: 1, leftHandle: nodes[1], rightHandle: nodes[1], handle: out var last, reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryClaimPair(poolOrdinal: 1, leftHandle: nodes[0], rightHandle: nodes[1], handle: out var earlier, reason: out reason), userMessage: reason);
        Assert.Equal(expected: new[] { 16, 288 }, actual: arena.SnapshotPool(poolOrdinal: 1).Select(selector: handle => handle.Slot));
        Assert.True(condition: arena.TryWrite(handle: last, fieldOrdinal: 0, value: CellValue.Int(value: 41), reason: out reason), userMessage: reason);
        var rows = Enumerable.Range(start: 0, count: arena.Layout.RowCount).ToArray();
        var plan = new ArenaUndoPlan(Name: "turn", Rows: rows, Depth: 1);

        arena.ConfigureUndo(plans: [plan]);
        var before = arena.ComputeHash();

        arena.BeginUndoTurn(group: "turn");
        arena.BeginUndoPass(group: "turn");
        Assert.True(condition: arena.TryRelease(handle: nodes[1], reason: out reason), userMessage: reason);
        arena.EndUndoPass(group: "turn");
        Assert.Empty(collection: arena.SnapshotPool(poolOrdinal: 1));
        Assert.False(condition: arena.TryRead(fieldOrdinal: 0, handle: earlier, value: out _));

        // Persist with a pending turn, then rewind after restoring the live rows and history.
        var persisted = section with { Pools = arena.ToPools(), PairPools = arena.ToPairPools() };
        var restored = Build(section: persisted);

        restored.ConfigureUndo(plans: [plan]);
        Assert.True(condition: restored.TryImportUndoSnapshot(snapshot: arena.ExportUndoSnapshot(), reason: out reason), userMessage: reason);
        Assert.Equal(expected: arena.ComputeHash(), actual: restored.ComputeHash());
        restored.CommitUndoTurn(group: "turn");
        Assert.True(condition: restored.TryRewindTurn(group: "turn", reason: out reason), userMessage: reason);
        Assert.Equal(expected: before, actual: restored.ComputeHash());
        Assert.True(condition: restored.TryResolvePoolSlot(poolOrdinal: 1, slot: 288, handle: out var restoredLast));
        Assert.True(condition: restored.TryRead(handle: restoredLast, fieldOrdinal: 0, value: out var value));
        Assert.Equal(expected: 41L, actual: value.AsInt);
    }
    [Fact]
    public void AnInverseBoardSeesSparsePoolFieldsAndRewindsTheirMembership() {
        var section = Section(capacity: 130, initial: [new StatePoolSeed(Slot: 129)]);

        section = section with {
            Lattices = ArenaFixture.Section().Lattices,
            Records = [new StateRecord(Name: Name(value: "item"), Fields: [
                new StatePoolField(Name: Name(value: "cell"), Default: CellValue.Int(value: 0)),
                new StatePoolField(Name: Name(value: "code"), Default: CellValue.Int(value: 7)),
            ])],
        };
        var catalog = StateCatalog.Compile(section: section);
        var fields = catalog.Pools[0].Fields;

        section = section with {
            Rows = [new StateRow(Name: Name(value: "board"), Kind: CellKind.Int,
            Domain: new StateDomain.CellsOf(Empty: -1, Topology: "map"),
            Inverse: new StateInverse(Tokens: Name(value: catalog.Descriptors[fields[0].RowOrdinal].Name), Codes: Name(value: catalog.Descriptors[fields[1].RowOrdinal].Name)))],
        };
        var arena = Build(section: section);
        var handle = Assert.Single(collection: arena.SnapshotPool(poolOrdinal: 0));

        Assert.True(condition: arena.TryReadAt(position: 0, rowOrdinal: 0, value: out var code));
        Assert.Equal(expected: 7L, actual: code.AsInt);
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryRelease(handle: handle, reason: out var reason), userMessage: reason);
        Assert.False(condition: arena.TryReadAt(position: 0, rowOrdinal: 0, value: out _));
        Assert.True(condition: arena.TryClaim(handle: out _, poolOrdinal: 0, reason: out reason), userMessage: reason);
        Assert.True(condition: arena.TryReadAt(position: 0, rowOrdinal: 0, value: out code));
        Assert.Equal(expected: 7L, actual: code.AsInt);
        arena.Rewind(mark: mark);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
    }
    [Fact]
    public void UndoCannotMoveAPoolMemberToAnotherIdentitySlot() {
        var arena = Build(section: Section(capacity: 4, initial: [new StatePoolSeed(Slot: 2)]));
        var row = arena.Catalog.Pools[0].DomainRowOrdinal;
        var at = arena.Layout[row].CellStart;

        arena.ConfigureUndo(plans: [new ArenaUndoPlan(Name: "turn", Rows: [row], Depth: 1)]);
        var before = arena.ComputeHash();
        var snapshot = new ArenaUndoSnapshot(Groups: [new ArenaUndoGroupSnapshot(Name: "turn", Depth: 1, Rows: [row], Pending: null, Segments: [new ArenaUndoSegmentSnapshot(Rewindable: true, Entries: [
            new ArenaUndoEntrySnapshot(ArenaColumn.MemberKey, (at + 2), -1, null, null, null, null, null),
            new ArenaUndoEntrySnapshot(ArenaColumn.Presence, (at + 2), 0, null, null, null, null, null),
            new ArenaUndoEntrySnapshot(ArenaColumn.MemberKey, (at + 1), 0, null, null, null, "2", null),
            new ArenaUndoEntrySnapshot(ArenaColumn.Presence, (at + 1), 1, null, null, null, null, null),
        ])])]);

        Assert.False(condition: arena.TryImportUndoSnapshot(snapshot: snapshot, reason: out var reason));
        Assert.Contains(expectedSubstring: "fixed identity slot", actualString: reason);
        Assert.Equal(expected: before, actual: arena.ComputeHash());
    }
}
