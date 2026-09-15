using Xunit;

namespace Puck.State.Tests;

public sealed class VectorCostTests {
    private sealed class TestRuleReader : IRuleReader {
        public required StateStore Store { get; init; }
        public string? BoundEachKey => null;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; } = StateCatalog.Compile(section: null);
        public Span<long> PatternWord => [];
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public bool TableKeyMissing { get; set; }
        public ulong Tick => 0UL;
        public ulong EngineTick => 0UL;

        public long BindingValue(int ordinal) => 0L;
        public Span<long> BoardScratch(int cells) => [];
        public int BoundIndex(BoundKey key) => -1;
        public void ReportTableKeyMissing(string table, long key) => TableKeyMissing = true;
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
    }

    [Fact]
    public void VectorCosts_MatchCostTable_At256Dimensions() {
        var space = new StateSpace(Name: CellName.Parse("lore"), Model: "text-embedding-3-small", Revision: "1", Dimensions: 256);
        var dummyKey = CellName.Parse("k1");

        var comps = new sbyte[256];
        comps[0] = 127;
        Assert.True(StateVector.TryCreate(components: comps, vector: out var dummyVector, error: out _));

        var left = new CompiledVectorOperand(rowOrdinal: 0, handle: default, rowName: "t", key: "k1", keyFrom: null, cellKey: dummyKey, space: space);
        var right = new CompiledVectorOperand(constant: dummyVector!, space: space);

        // dot: 2 + d = 258
        var dotCall = new VectorCallOperand(operation: ExpressionOp.Dot, left: left, right: right, valueKind: CellKind.Int);
        Assert.Equal(258L, dotCall.Cost(null!));

        // similarity: 2 + 3d = 770
        var simCall = new VectorCallOperand(operation: ExpressionOp.Similarity, left: left, right: right, valueKind: CellKind.Fixed);
        Assert.Equal(770L, simCall.Cost(null!));

        // identical: 2 + d = 258
        var idCall = new VectorCallOperand(operation: ExpressionOp.Identical, left: left, right: right, valueKind: CellKind.Int);
        Assert.Equal(258L, idCall.Cost(null!));

        // copy: d = 256
        var copy = new VectorCopyEffect(row: "t", key: "k1", keyFrom: null, handle: default, cellKey: dummyKey, rowOrdinal: 0, source: right, describe: "copy");
        Assert.Equal(256L, copy.Cost(null!));

        // mix: (terms + 1) * d = (3 + 1) * 256 = 1024
        MixTermFact[] terms = [
            new(left, 1),
            new(right, 2),
            new(left, 3)
        ];
        var mix = new VectorMixEffect(row: "t", key: "k1", keyFrom: null, handle: default, cellKey: dummyKey, rowOrdinal: 0, terms: terms, dimensions: 256, describe: "mix");
        Assert.Equal(1024L, mix.Cost(null!));

        // mean: (capacity + 1) * d = (128 + 1) * 256 = 33024 (without where)
        var meanNoWhere = new VectorMeanEffect(row: "t", key: "k1", keyFrom: null, handle: default, cellKey: dummyKey, rowOrdinal: 0, fromRowOrdinal: 1, fromRowName: "src", fromHandle: default, dimensions: 256, fromCapacity: 128, whereRowOrdinal: null, whereRowName: null, whereHandle: default, describe: "mean");
        Assert.Equal(33024L, meanNoWhere.Cost(null!));

        // mean: (capacity + 1) * d + capacity = 33024 + 128 = 33152 (with where)
        var meanWithWhere = new VectorMeanEffect(row: "t", key: "k1", keyFrom: null, handle: default, cellKey: dummyKey, rowOrdinal: 0, fromRowOrdinal: 1, fromRowName: "src", fromHandle: default, dimensions: 256, fromCapacity: 128, whereRowOrdinal: 2, whereRowName: "whereRow", whereHandle: default, describe: "mean");
        Assert.Equal(33152L, meanWithWhere.Cost(null!));

        // nearest into Int: capacity * (d + 2) = 128 * 258 = 33024 (without where)
        var nearestIntNoWhere = new VectorNearestEffect(intoRowOrdinal: 0, intoRowName: "into", intoHandle: default, intoKind: CellKind.Int, isIntoSlot: false, fromRowOrdinal: 1, fromRowName: "from", fromHandle: default, dimensions: 256, fromCapacity: 128, query: left, k: 3, threshold: null, farthest: false, whereRowOrdinal: null, whereRowName: null, whereHandle: default, excludeKey: null, excludeKeyFrom: null, excludeCellKey: default, describe: "nearest");
        Assert.Equal(33024L, nearestIntNoWhere.Cost(null!));

        // nearest into Int: 128 * 258 + 128 = 33152 (with where)
        var nearestIntWithWhere = new VectorNearestEffect(intoRowOrdinal: 0, intoRowName: "into", intoHandle: default, intoKind: CellKind.Int, isIntoSlot: false, fromRowOrdinal: 1, fromRowName: "from", fromHandle: default, dimensions: 256, fromCapacity: 128, query: left, k: 3, threshold: null, farthest: false, whereRowOrdinal: 2, whereRowName: "whereRow", whereHandle: default, excludeKey: null, excludeKeyFrom: null, excludeCellKey: default, describe: "nearest");
        Assert.Equal(33152L, nearestIntWithWhere.Cost(null!));

        // nearest into Fixed: capacity * (3d + 2) = 128 * (3 * 256 + 2) = 128 * 770 = 98560 (without where)
        var nearestFixedNoWhere = new VectorNearestEffect(intoRowOrdinal: 0, intoRowName: "into", intoHandle: default, intoKind: CellKind.Fixed, isIntoSlot: false, fromRowOrdinal: 1, fromRowName: "from", fromHandle: default, dimensions: 256, fromCapacity: 128, query: left, k: 3, threshold: null, farthest: false, whereRowOrdinal: null, whereRowName: null, whereHandle: default, excludeKey: null, excludeKeyFrom: null, excludeCellKey: default, describe: "nearest");
        Assert.Equal(98560L, nearestFixedNoWhere.Cost(null!));

        // nearest into Text: 98560 + 128 = 98688 (with where)
        var nearestTextWithWhere = new VectorNearestEffect(intoRowOrdinal: 0, intoRowName: "into", intoHandle: default, intoKind: CellKind.Text, isIntoSlot: true, fromRowOrdinal: 1, fromRowName: "from", fromHandle: default, dimensions: 256, fromCapacity: 128, query: left, k: 1, threshold: null, farthest: false, whereRowOrdinal: 2, whereRowName: "whereRow", whereHandle: default, excludeKey: null, excludeKeyFrom: null, excludeCellKey: default, describe: "nearest");
        Assert.Equal(98688L, nearestTextWithWhere.Cost(null!));

        // remember: capacity * 3d + d = 128 * 3 * 256 + 256 = 98304 + 256 = 98560
        var remember = new VectorRememberEffect(row: "t", key: "k1", keyFrom: null, handle: default, cellKey: dummyKey, rowOrdinal: 0, dimensions: 256, capacity: 128, source: left, unlessWithinQ16: 58982L, describe: "remember");
        Assert.Equal(98560L, remember.Cost(null!));
    }

    [Fact]
    public void LiteralKey_Dot_Evaluates_WithoutAllocation() {
        var space = new StateSpace(Name: CellName.Parse("lore"), Model: "text-embedding-3-small", Revision: "1", Dimensions: 8);
        var key1 = CellName.Parse("k1");
        var key2 = CellName.Parse("k2");

        sbyte[] comps1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] comps2 = [0, 127, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: comps1, vector: out var vec1, error: out _));
        Assert.True(StateVector.TryCreate(components: comps2, vector: out var vec2, error: out _));

        var table = new StateRow(
            Name: CellName.Parse("vectors"),
            Kind: CellKind.Vector,
            Space: "lore",
            Capacity: 10,
            Cells: [
                new StateCell(Key: key1, Vector: vec1!),
                new StateCell(Key: key2, Vector: vec2!),
            ]
        );

        StateRow[] rows = [table];
        var layout = new FrameLayout(
            rows: rows,
            topology: static _ => null,
            spaces: name => (name == "lore") ? space : null
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        var initialStore = new RowStore(rows: rows);
        frame.Load(source: initialStore);

        var reader = new TestRuleReader { Store = frame };
        var left = new CompiledVectorOperand(rowOrdinal: 0, handle: default, rowName: "vectors", key: "k1", keyFrom: null, cellKey: key1, space: space);
        var right = new CompiledVectorOperand(rowOrdinal: 0, handle: default, rowName: "vectors", key: "k2", keyFrom: null, cellKey: key2, space: space);

        var dotOperand = new VectorCallOperand(operation: ExpressionOp.Dot, left: left, right: right, valueKind: CellKind.Int);

        // Warm up JIT and cache
        _ = dotOperand.Read(reader: reader);

        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var result = dotOperand.Read(reader: reader);
        var afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, result.Value); // orthogonal vectors: dot = 0
        Assert.Equal(0L, afterAlloc - beforeAlloc);
    }
}
