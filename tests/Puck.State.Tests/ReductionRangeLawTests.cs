using Xunit;

namespace Puck.State.Tests;

/// <summary>Range reductions read live values, preserve kinds, intersect keyed filters, and account for their reads.</summary>
public sealed class ReductionRangeLawTests {
    private static CellName Name(string text) => CellName.Parse(candidate: text);
    private static StateRow Row(string name, params long[] values) => new(
        Name(text: name),
        CellKind.Int,
        Capacity: Math.Max(
            val1: 1,
            val2: values.Length
        ),
        Cells: values.Select(selector: (value, index) => new StateCell(
            Name(text: index.ToString()),
            value
        )).ToArray()
    );

    [Fact]
    public void AKeyedReadResolvesValueAndAdvancingMetadataInOneWalk() {
        var cells = new CountedCells(cells: [.. Enumerable.Range(
                count: 32,
                start: 0
            ).Select(selector: index => new StateCell(
                Name(text: index.ToString()),
                index,
                Advance: new StateAdvance(
                    2,
                    1
                )
            ))]);
        var row = Row("values") with { Cells = cells };

        StateReader.ReadCell(
            new RowStore(rows: [row]),
            row,
            "31",
            3,
            3,
            out var value,
            out _
        );
        Assert.Equal(
            actual: value,
            expected: 37
        );
        Assert.Equal(
            32,
            cells.Reads
        );
    }
    [Fact]
    public void BothFiltersIntersectInEitherOrderAndDeclareBothReadDependencies() {
        var reader = new Reader(
            Row(
                "values",
                -1,
                0,
                1,
                63,
                64
            ),
            Row(
                "eligible",
                1,
                1,
                0,
                1
            )
        );
        const string Expression = "$reduce:sum:values:between:0:63:where:eligible";

        Assert.Equal(
            63,
            reader.Evaluate(Expression)
        );
        Assert.Equal(
            63,
            reader.Evaluate("$reduce:sum:values:where:eligible:between:0:63")
        );
        Assert.Equal(
            2,
            reader.Evaluate("$reduce:count:values:where:eligible:between:0:63")
        );
        var operand = Assert.IsType<ReductionOperand>(@object: Assert.Single(collection: reader.Compile(Expression)).Operand);
        List<RuleAccess> reads = [];

        operand.CollectReads(into: reads);
        Assert.Contains(
            new RuleAccess(
                "values",
                null
            ),
            reads
        );
        Assert.Contains(
            new RuleAccess(
                "eligible",
                null
            ),
            reads
        );
        Assert.Equal(
            15,
            operand.Cost(context: reader.Context)
        );
    }
    [Fact]
    public void FilterLookupsAvoidRepeatedFullScansAndObserveInPlaceReplacement() {
        const int Count = 512;
        var values = Row(
            "values",
            [.. Enumerable.Repeat(
                    count: Count,
                    element: 2L
                )]
        );
        var members = Row(
            "eligible",
            [.. Enumerable.Repeat(
                    count: Count,
                    element: 1L
                )]
        ).Cells!.ToArray();
        var cells = new CountedCells(cells: members);
        var filter = Row("eligible") with { Cells = cells };
        var store = new RowStore(rows: [values, filter]);

        Assert.Equal(
            (2 * Count),
            StateReader.ReduceRaw(
                filter: filter,
                op: StateReduceOp.Sum,
                range: null,
                row: values,
                store: store,
                tick: 0,
                engineTick: 0
            )
        );
        // Allow hash collisions, but rule out the former two full key scans per source cell (over 260,000 reads).
        Assert.InRange(
            cells.Reads,
            Count,
            (32 * Count)
        );
        Array.Reverse(array: members);
        members[0] = new StateCell(
            Name(text: "outside"),
            1
        );
        members[1] = members[1] with { Value = 0, Advance = new StateAdvance(
            1,
            1
        ) };
        Assert.Equal(
            (2 * (Count - 2)),
            StateReader.ReduceRaw(
                filter: filter,
                op: StateReduceOp.Sum,
                range: null,
                row: values,
                store: store,
                tick: 0,
                engineTick: 0
            )
        );
        Assert.Equal(
            (2 * (Count - 1)),
            StateReader.ReduceRaw(
                filter: filter,
                op: StateReduceOp.Sum,
                range: null,
                row: values,
                store: store,
                tick: 1,
                engineTick: 1
            )
        );
        Assert.Equal(
            Count,
            StateReader.ReduceRaw(
                op: StateReduceOp.Count,
                row: values,
                store: store,
                tick: 1,
                engineTick: 1
            )
        );
    }
    [Fact]
    public void FixedBoundsUseSourceUnitsAndAdvancingValuesAreReadAtTheCurrentTick() {
        var row = Row(
            "values",
            -65536,
            0,
            65536
        ) with { Kind = CellKind.Fixed };

        row = row with { Cells = row.Cells!.Select(selector: cell => cell with { Advance = new StateAdvance(
            1,
            2
        ) }).ToArray() };
        var reader = new Reader(row) { Tick = 1 };

        Assert.Equal(
            2,
            reader.Evaluate("$reduce:count:values:between:0.5:1.5")
        );
        Assert.Equal(
            131072,
            reader.Evaluate(
                kind: CellKind.Fixed,
                text: "$reduce:sum:values:between:0.5:1.5"
            )
        );
        reader.Tick = 5;
        Assert.Equal(
            1,
            reader.Evaluate("$reduce:count:values:between:0.5:1.5")
        );
        Assert.Equal(
            3,
            reader.Evaluate("$reduce:count:values")
        );
    }
    [InlineData("count", 3)]
    [InlineData("sum", 64)]
    [InlineData("min", 0)]
    [InlineData("max", 63)]
    [Theory]
    public void InclusiveRangeFiltersEveryAggregateAndEmptyIsZero(string op, long expected) {
        var reader = new Reader(Row(
            "values",
            -1,
            0,
            1,
            63,
            64
        ));

        Assert.Equal(
            expected,
            reader.Evaluate($"$reduce:{op}:values:between:0:63")
        );
        Assert.Equal(
            0,
            reader.Evaluate($"$reduce:{op}:values:between:2:62")
        );
        Assert.Equal(
            0,
            new Reader(Row("values")).Evaluate($"$reduce:{op}:values:between:0:63")
        );
        Assert.Equal(
            5,
            reader.Evaluate("$reduce:count:values")
        );
    }
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
    [Theory]
    public void MalformedRangesAndIncompatibleRanksRefuse(string expression) {
        var reader = new Reader(Row(
            "values",
            0
        ));
        // A token name also covers malformed suffixes that the human expression lexer would reject first.
        Assert.Throws<RuleException>(testCode: () => RuleCompiler.CompileExpression(
            new ValueExpression(Tokens: [new ValueToken.State(expression)]),
            CellKind.Int,
            "range-law",
            "range-law",
            reader.Context
        ));
    }
    [Fact]
    public void ScratchFrameWritesChangeTheRangeResultWithoutChangingTheSourceRows() {
        var row = Row(
            "values",
            -1,
            0,
            63
        );
        var reader = new Reader(row);
        var frame = new StateFrame(
            layout: new FrameLayout(
                rows: reader.Rows,
                topology: _ => null
            ),
            rows: reader.Rows
        );

        frame.Load(source: reader.Store);
        Assert.True(condition: frame.TryWrite(
            row,
            Name(text: "0"),
            1,
            StateWriteKind.Set,
            out _
        ));
        Assert.Equal(
            2,
            reader.Evaluate("$reduce:count:values:between:0:63")
        );
        reader.Store = frame;
        Assert.Equal(
            3,
            reader.Evaluate("$reduce:count:values:between:0:63")
        );
        Assert.Equal(
            -1,
            row.Cells![0].Value
        );
    }

    private sealed class Reader : IRuleReader, IStateSection {
        public Reader(params StateRow[] rows) { Rows = rows; Catalog = StateCatalog.Compile(section: this); Store = new RowStore(rows: rows); }

        public string? BoundEachKey => null;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; }
        public RuleCompileContext Context => new(
            this,
            Catalog,
            null,
            null,
            null,
            240,
            RuleVocabulary.Core
        );
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public Span<long> PatternWord => [];
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public IReadOnlyList<StateRow> Rows { get; }
        public StateStore Store { get; set; }
        public bool TableKeyMissing { get; set; }
        public ulong Tick { get; set; }
        public ulong EngineTick { get; set; }

        public long BindingValue(int ordinal) => 0;
        public Span<long> BoardScratch(int cells) => new long[cells];
        public int BoundIndex(BoundKey key) => -1;
        public CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) =>
            RuleCompiler.CompileExpression(
                ValueExpression.Parse(text: text),
                kind,
                "range-law",
                "range-law",
                Context
            );
        public long Evaluate(string text, CellKind kind = CellKind.Int) {
            Assert.True(condition: RuleEvaluation.TryEvaluateExpression(
                this,
                Compile(
                    kind: kind,
                    text: text
                ),
                kind,
                out var value
            ));
            return value;
        }
        public void ReportTableKeyMissing(string table, long key) => TableKeyMissing = true;
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
    }
    private sealed class CountedCells(StateCell[] cells) : IReadOnlyList<StateCell> {
        public int Count => cells.Length;
        public int Reads { get; set; }

        public StateCell this[int index] { get { Reads++; return cells[index]; } }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IEnumerator<StateCell> GetEnumerator() => ((IEnumerable<StateCell>)cells).GetEnumerator();
    }
}
