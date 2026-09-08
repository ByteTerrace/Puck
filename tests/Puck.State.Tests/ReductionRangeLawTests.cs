using Xunit;

namespace Puck.State.Tests;

/// <summary>Range reductions read live values, preserve kinds, intersect keyed filters, and account for their reads.</summary>
public sealed class ReductionRangeLawTests {
    private static CellName Name(string text) => CellName.Parse(text);
    private static StateRow Row(string name, params long[] values) => new(Name(name), CellKind.Int, Capacity: Math.Max(1, values.Length),
        Cells: values.Select((value, index) => new StateCell(Name(index.ToString()), value)).ToArray());

    private sealed class Reader : IRuleReader, IStateSection {
        public Reader(params StateRow[] rows) { Rows = rows; Catalog = StateCatalog.Compile(this); Store = new RowStore(rows); }
        public IReadOnlyList<StateRow> Rows { get; }
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public StateStore Store { get; set; }
        public StateCatalog Catalog { get; }
        public ulong Tick { get; set; }
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public string? BoundEachKey => null;
        public string? BoundTokenKey { get; set; }
        public string? BoundPreviousKey { get; set; }
        public bool TableKeyMissing { get; set; }
        public Span<long> PatternWord => [];
        public int BoundIndex(BoundKey key) => -1;
        public long BindingValue(int ordinal) => 0;
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
        public void ReportTableKeyMissing(string table, long key) => TableKeyMissing = true;
        public Span<long> BoardScratch(int cells) => new long[cells];
        public RuleCompileContext Context => new(this, Catalog, null, null, null, 240, RuleVocabulary.Core);
        public CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) =>
            RuleCompiler.CompileExpression(ValueExpression.Parse(text), kind, "range-law", "range-law", Context);
        public long Evaluate(string text, CellKind kind = CellKind.Int) {
            Assert.True(RuleEvaluation.TryEvaluateExpression(this, Compile(text, kind), kind, out var value));
            return value;
        }
    }

    private sealed class CountedCells(StateCell[] cells) : IReadOnlyList<StateCell> {
        public int Reads { get; set; }
        public int Count => cells.Length;
        public StateCell this[int index] { get { Reads++; return cells[index]; } }
        public IEnumerator<StateCell> GetEnumerator() => ((IEnumerable<StateCell>)cells).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void AKeyedReadResolvesValueAndAdvancingMetadataInOneWalk() {
        var cells = new CountedCells([.. Enumerable.Range(0, 32).Select(index => new StateCell(Name(index.ToString()), index, Advance: new StateAdvance(2, 1)))]);
        var row = Row("values") with { Cells = cells };
        StateReader.ReadCell(new RowStore([row]), row, "31", 3, out var value, out _);
        Assert.Equal(37, value);
        Assert.Equal(32, cells.Reads);
    }

    [Fact]
    public void FilterLookupsAvoidRepeatedFullScansAndObserveInPlaceReplacement() {
        const int count = 512;
        var values = Row("values", [.. Enumerable.Repeat(2L, count)]);
        var members = Row("eligible", [.. Enumerable.Repeat(1L, count)]).Cells!.ToArray();
        var cells = new CountedCells(members);
        var filter = Row("eligible") with { Cells = cells };
        var store = new RowStore([values, filter]);
        Assert.Equal(2 * count, StateReader.ReduceRaw(store, values, StateReduceOp.Sum, 0, filter, null));
        // Allow hash collisions, but rule out the former two full key scans per source cell (over 260,000 reads).
        Assert.InRange(cells.Reads, count, 32 * count);
        Array.Reverse(members);
        members[0] = new StateCell(Name("outside"), 1);
        members[1] = members[1] with { Value = 0, Advance = new StateAdvance(1, 1) };
        Assert.Equal(2 * (count - 2), StateReader.ReduceRaw(store, values, StateReduceOp.Sum, 0, filter, null));
        Assert.Equal(2 * (count - 1), StateReader.ReduceRaw(store, values, StateReduceOp.Sum, 1, filter, null));
        Assert.Equal(count, StateReader.ReduceRaw(store, values, StateReduceOp.Count, 1));
    }

    [Theory]
    [InlineData("count", 3)]
    [InlineData("sum", 64)]
    [InlineData("min", 0)]
    [InlineData("max", 63)]
    public void InclusiveRangeFiltersEveryAggregateAndEmptyIsZero(string op, long expected) {
        var reader = new Reader(Row("values", -1, 0, 1, 63, 64));
        Assert.Equal(expected, reader.Evaluate($"$reduce:{op}:values:between:0:63"));
        Assert.Equal(0, reader.Evaluate($"$reduce:{op}:values:between:2:62"));
        Assert.Equal(0, new Reader(Row("values")).Evaluate($"$reduce:{op}:values:between:0:63"));
        Assert.Equal(5, reader.Evaluate("$reduce:count:values"));
    }

    [Fact]
    public void BothFiltersIntersectInEitherOrderAndDeclareBothReadDependencies() {
        var reader = new Reader(Row("values", -1, 0, 1, 63, 64), Row("eligible", 1, 1, 0, 1));
        const string expression = "$reduce:sum:values:between:0:63:where:eligible";
        Assert.Equal(63, reader.Evaluate(expression));
        Assert.Equal(63, reader.Evaluate("$reduce:sum:values:where:eligible:between:0:63"));
        Assert.Equal(2, reader.Evaluate("$reduce:count:values:where:eligible:between:0:63"));
        var operand = Assert.IsType<ReductionOperand>(Assert.Single(reader.Compile(expression)).Operand);
        List<RuleAccess> reads = [];
        operand.CollectReads(reads);
        Assert.Contains(new RuleAccess("values", null), reads);
        Assert.Contains(new RuleAccess("eligible", null), reads);
        Assert.Equal(15, operand.Cost(reader.Context));
    }

    [Fact]
    public void FixedBoundsUseSourceUnitsAndAdvancingValuesAreReadAtTheCurrentTick() {
        var row = Row("values", -65536, 0, 65536) with { Kind = CellKind.Fixed };
        row = row with { Cells = row.Cells!.Select(cell => cell with { Advance = new StateAdvance(1, 2) }).ToArray() };
        var reader = new Reader(row) { Tick = 1 };
        Assert.Equal(2, reader.Evaluate("$reduce:count:values:between:0.5:1.5"));
        Assert.Equal(131072, reader.Evaluate("$reduce:sum:values:between:0.5:1.5", CellKind.Fixed));
        reader.Tick = 5;
        Assert.Equal(1, reader.Evaluate("$reduce:count:values:between:0.5:1.5"));
        Assert.Equal(3, reader.Evaluate("$reduce:count:values"));
    }

    [Fact]
    public void ScratchFrameWritesChangeTheRangeResultWithoutChangingTheSourceRows() {
        var row = Row("values", -1, 0, 63);
        var reader = new Reader(row);
        var frame = new StateFrame(new FrameLayout(reader.Rows, _ => null), reader.Rows);
        frame.Load(reader.Store);
        Assert.True(frame.TryWrite(row, Name("0"), 1, StateWriteKind.Set, out _));
        Assert.Equal(2, reader.Evaluate("$reduce:count:values:between:0:63"));
        reader.Store = frame;
        Assert.Equal(3, reader.Evaluate("$reduce:count:values:between:0:63"));
        Assert.Equal(-1, row.Cells![0].Value);
    }

    [Theory]
    [InlineData("$reduce:count:values:between:63:0")]
    [InlineData("$reduce:count:values:between:0")]
    [InlineData("$reduce:count:values:between:x:2")]
    [InlineData("$reduce:count:values:between:0:1:between:0:1")]
    [InlineData("$reduce:count:values:where:values:where:values")]
    [InlineData("$reduce:count:values:where:")]
    [InlineData("$reduce:count:values:between:0:1:extra")]
    [InlineData("$reduce:count:values:between:0:9223372036854775808")]
    [InlineData("$reduce:count:missing:between:0:1")]
    [InlineData("$reduce:arrangementRank:values:between:0:1")]
    public void MalformedRangesAndIncompatibleRanksRefuse(string expression) {
        var reader = new Reader(Row("values", 0));
        // A token name also covers malformed suffixes that the human expression lexer would reject first.
        Assert.Throws<RuleException>(() => RuleCompiler.CompileExpression(new ValueExpression([new ValueToken.State(expression)]),
            CellKind.Int, "range-law", "range-law", reader.Context));
    }
}
