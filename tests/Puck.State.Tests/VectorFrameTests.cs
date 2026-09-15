using Xunit;

namespace Puck.State.Tests;

public sealed class VectorFrameTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static int Ordinal(FrameLayout layout, string name) {
        Assert.True(layout.TryOrdinal(name, out var ord));
        return ord;
    }

    private static StateSpace TestSpace(string name = "testSpace", int dimensions = 8) => new(
        Name: Name(name),
        Model: "test-model",
        Revision: "1",
        Dimensions: dimensions
    );

    private static StateRow VectorSlot(string name, string spaceName, StateVector? vector = null) => new(
        Name: Name(name),
        Kind: CellKind.Vector,
        Space: spaceName,
        Cells: (vector is not null) ? [new StateCell(Key: StateRow.SlotKey, Vector: vector)] : null
    );

    private static StateRow VectorTable(string name, string spaceName, int capacity, params (string Key, StateVector Vector)[] entries) => new(
        Name: Name(name),
        Kind: CellKind.Vector,
        Space: spaceName,
        Capacity: capacity,
        Cells: entries.Select(e => new StateCell(Key: Name(e.Key), Vector: e.Vector)).ToArray()
    );

    [Fact]
    public void Correction1_FrameOverrunWhenVectorTableGrows_FitsReturnsFalse() {
        var space = TestSpace();
        sbyte[] comps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(comps, out var v1, out _));

        var initialTable = VectorTable("memories", "testSpace", capacity: 4, ("m1", v1!));
        var layout = new FrameLayout(
            rows: [initialTable],
            topology: static _ => null,
            spaces: name => (name == "testSpace") ? space : null
        );

        Assert.True(layout.Fits(rows: [initialTable]));

        // Increase the vector table's capacity/cell count: Fits must detect the change and return false
        var grownTable = VectorTable("memories", "testSpace", capacity: 8, ("m1", v1!), ("m2", v1!));
        Assert.False(layout.Fits(rows: [grownTable]));
    }

    [Fact]
    public void Correction2_LoadCacheAliasingAcrossRows_PerCellCacheAndZeroing() {
        var space = TestSpace();
        sbyte[] sharedComps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(sharedComps, out var sharedVec, out _));

        // Both tables share the EXACT SAME StateVector instance across both rows.
        // A single-field cache would alias when loading the second row; per-cell cache correctly loads both.
        var tableA = VectorTable("tableA", "testSpace", capacity: 2, ("item1", sharedVec!));
        var tableB = VectorTable("tableB", "testSpace", capacity: 2, ("item1", sharedVec!));

        var rows = new StateRow[] { tableA, tableB };
        var layout = new FrameLayout(
            rows: rows,
            topology: static _ => null,
            spaces: name => (name == "testSpace") ? space : null
        );

        var ordA = Ordinal(layout, "tableA");
        var ordB = Ordinal(layout, "tableB");

        var frame = new StateFrame(layout: layout, rows: rows);
        var initialStore = new RowStore(rows: rows);
        frame.Load(source: initialStore);

        // Read both rows: both must have loaded sharedVec
        Assert.True(frame.TryStoredVector(tableA, Name("item1"), out var readA));
        Assert.True(frame.TryStoredVector(tableB, Name("item1"), out var readB));
        Assert.Equal(127, readA[0]);
        Assert.Equal(127, readB[0]);

        var versionA = frame.RowVersion(ordA);
        var versionB = frame.RowVersion(ordB);
        Assert.True(versionA > 0);
        Assert.True(versionB > 0);

        // Reloading an unchanged document must leave row versions unchanged
        frame.Load(source: initialStore);
        Assert.Equal(versionA, frame.RowVersion(ordA));
        Assert.Equal(versionB, frame.RowVersion(ordB));

        // Now reload from a store where tableA's vector is missing/cleared
        var clearedTableA = new StateRow(
            Name: Name("tableA"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 2,
            Cells: [new StateCell(Key: Name("item1"), Vector: null)]
        );
        frame.Load(source: new RowStore(rows: [clearedTableA, tableB]));

        Assert.True(frame.TryStoredVector(clearedTableA, Name("item1"), out var clearedReadA));
        for (var i = 0; i < 8; i++) {
            Assert.Equal(0, clearedReadA[i]);
        }
        // tableA version bumped on clearing
        Assert.True(frame.RowVersion(ordA) > versionA);

        // tableB remains unaffected and its version unchanged
        Assert.True(frame.TryStoredVector(tableB, Name("item1"), out var retainedReadB));
        Assert.Equal(127, retainedReadB[0]);
        Assert.Equal(versionB, frame.RowVersion(ordB));
    }

    [Fact]
    public void Correction3_UnframedVectorReads_FallbackReadsStore() {
        sbyte[] comps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(comps, out var v, out _));

        var unframedRow = VectorSlot("unframedSlot", "testSpace", v);
        var store = new RowStore(rows: [unframedRow]);

        // Frame built WITHOUT a space resolver: vector row is unframed
        var layout = new FrameLayout(
            rows: [unframedRow],
            topology: static _ => null,
            spaces: null
        );

        var frame = new StateFrame(layout: layout, rows: [unframedRow]);
        frame.Load(source: store);

        // Fallback read against underlying store must succeed
        Assert.True(frame.TryStoredVector(unframedRow, StateRow.SlotKey, out var readComps));
        Assert.Equal(127, readComps[0]);
    }

    [Fact]
    public void Correction4_ResolvedTransforms_CopyMixMean_ZeroAllocationsAndExactResults() {
        var space = TestSpace();
        sbyte[] c1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] c2 = [0, 127, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(c1, out var v1, out _));
        Assert.True(StateVector.TryCreate(c2, out var v2, out _));

        var sourceTable = VectorTable("sources", "testSpace", capacity: 4, ("s1", v1!), ("s2", v2!));
        var targetSlot = VectorSlot("target", "testSpace");

        var rows = new StateRow[] { sourceTable, targetSlot };
        var layout = new FrameLayout(
            rows: rows,
            topology: static _ => null,
            spaces: name => (name == "testSpace") ? space : null
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        var sourceOrd = Ordinal(layout, "sources");
        var targetOrd = Ordinal(layout, "target");


        // 2. Mix transform (no allocations)
        var mix = new ResolvedVectorTransform.Mix(
            TargetRowOrdinal: targetOrd,
            TargetRowName: "target",
            TargetKey: StateRow.SlotKey,
            Terms: [
                new ResolvedMixTerm(sourceOrd, "sources", Name("s1"), null, 1),
                new ResolvedMixTerm(sourceOrd, "sources", Name("s2"), null, 1)
            ]
        );

        // Warm up JIT
        frame.TryApplyVector(mix, out _);

        var allocatedBeforeMix = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(frame.TryApplyVector(mix, out var reasonMix), reasonMix);
        var allocatedAfterMix = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocatedAfterMix - allocatedBeforeMix);

        Assert.True(frame.TryStoredVector(targetSlot, StateRow.SlotKey, out var mixed));
        // Equal weights on orthogonal unit vectors produce equal non-zero components on dims 0 and 1
        Assert.Equal(mixed[0], mixed[1]);
        Assert.True(mixed[0] > 0);

        // 3. Mean transform (no allocations)
        var mean = new ResolvedVectorTransform.Mean(
            TargetRowOrdinal: targetOrd,
            TargetRowName: "target",
            TargetKey: StateRow.SlotKey,
            FromRowOrdinal: sourceOrd,
            FromRowName: "sources"
        );

        // Warm up JIT
        frame.TryApplyVector(mean, out _);

        var allocatedBeforeMean = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(frame.TryApplyVector(mean, out var reasonMean), reasonMean);
        var allocatedAfterMean = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocatedAfterMean - allocatedBeforeMean);

        Assert.True(frame.TryStoredVector(targetSlot, StateRow.SlotKey, out var meanComps));
        Assert.Equal(meanComps[0], meanComps[1]);
        Assert.True(meanComps[0] > 0);
    }

    [Fact]
    public void Layout_ZeroLengthBufferWithoutVectorRows() {
        var intRow = new StateRow(Name: Name("counter"), Kind: CellKind.Int, Cells: [new StateCell(Key: StateRow.SlotKey, Value: 42L)]);
        var layout = new FrameLayout(rows: [intRow], topology: static _ => null);
        var frame = new StateFrame(layout: layout, rows: [intRow]);

        Assert.Equal(0, layout.VectorLength);
        Assert.Equal(0, layout.VectorCellCount);
        Assert.True(frame.VectorValues.IsEmpty);
    }

    [Fact]
    public void CopyFrom_CopiesVectorsAcrossFrames() {
        var space = TestSpace();
        sbyte[] comps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(comps, out var v, out _));

        var targetSlot = VectorSlot("target", "testSpace");
        var rows = new StateRow[] { targetSlot };
        var layout = new FrameLayout(rows: rows, topology: static _ => null, spaces: _ => space);

        var frame1 = new StateFrame(layout: layout, rows: rows);
        frame1.Load(source: new RowStore(rows: rows));
        Assert.True(frame1.TryWriteVector(Ordinal(layout, "target"), StateRow.SlotKey, v!.Components, out var reason), reason);

        var frame2 = new StateFrame(layout: layout, rows: rows);
        frame2.Load(source: new RowStore(rows: rows));

        frame2.CopyFrom(frame1);

        Assert.True(frame2.TryStoredVector(targetSlot, StateRow.SlotKey, out var readComps));
        Assert.Equal(127, readComps[0]);
    }

    [Fact]
    public void JournalRewind_AcrossInterleavedScalarAndVectorWrites() {
        var space = TestSpace();
        sbyte[] c1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] c2 = [0, 127, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(c1, out var v1, out _));
        Assert.True(StateVector.TryCreate(c2, out var v2, out _));

        var scalarRow = new StateRow(Name: Name("counter"), Kind: CellKind.Int, Cells: [new StateCell(Key: StateRow.SlotKey, Value: 10L)]);
        var vectorRow = VectorSlot("vec", "testSpace", v1);

        var rows = new StateRow[] { scalarRow, vectorRow };
        var layout = new FrameLayout(rows: rows, topology: static _ => null, spaces: _ => space);
        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        var mark = frame.BeginJournalScope();

        var vectorOrd = Ordinal(layout, "vec");

        Assert.True(frame.TryWrite(scalarRow, StateRow.SlotKey, 20L, StateWriteKind.Set, out _));
        Assert.True(frame.TryWriteVector(vectorOrd, StateRow.SlotKey, v2!.Components, out _));

        Assert.True(frame.TryStored(scalarRow, StateRow.SlotKey, out var modifiedVal, out _));
        Assert.Equal(20L, modifiedVal);
        Assert.True(frame.TryStoredVector(vectorRow, StateRow.SlotKey, out var modifiedVec));
        Assert.Equal(127, modifiedVec[1]);

        frame.RewindJournalScope(mark: mark);

        Assert.True(frame.TryStored(scalarRow, StateRow.SlotKey, out var restoredVal, out _));
        Assert.Equal(10L, restoredVal);
        Assert.True(frame.TryStoredVector(vectorRow, StateRow.SlotKey, out var restoredVec));
        Assert.Equal(127, restoredVec[0]);
        Assert.Equal(0, restoredVec[1]);
    }

    [Fact]
    public void StateFrameHash_ChangesOnOneVectorByte_AndIgnoresVectorlessRows() {
        var space = TestSpace();
        sbyte[] c1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] c2 = [126, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(c1, out var v1, out _));

        var vectorRow = VectorSlot("vec", "testSpace", v1);
        var rows = new StateRow[] { vectorRow };
        var layout = new FrameLayout(rows: rows, topology: static _ => null, spaces: _ => space);
        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        var hashBaseline = StateFrameHash.Compute(frame);

        // Mutate one byte
        Assert.True(frame.TryWriteVector(Ordinal(layout, "vec"), StateRow.SlotKey, c2, out _));
        var hashModified = StateFrameHash.Compute(frame);

        Assert.NotEqual(hashBaseline, hashModified);
    }
}
