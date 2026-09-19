using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using RuleOperand = Puck.State.Rules.RuleOperand;
using RuleReads = Puck.State.Rules.RuleReads;

namespace Puck.Cli.Bench;

// The section, arena and catalog the compiled-program scenarios read through: one Int row of keyed cells, and one
// cursor row whose value selects a key, so a direct read and a key indirection differ only in how the key arrives.
public sealed class StateKernelReader : IStateReader {
    public const int CellCount = 64;

    private const string CursorRow = "cursor";
    private const string ProbeRow = "probe";

    private readonly long[] m_boardScratch = new long[CellCount];
    private readonly long[] m_locals = new long[RuleCapacity.MaxLocalsPerRule];
    private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];

    public StateArena Arena { get; }
    public StateCatalog Catalog { get; }
    public int Cursor { get; }
    public ulong EngineTick => 0UL;
    public int Probe { get; }
    public IReadOnlyList<CellKey> ProbeKeys { get; }
    public ulong Tick => 0UL;

    public StateKernelReader(int cells) {
        var probe = new StateCell[cells];

        for (var i = 0; (i < cells); ++i) {
            probe[i] = new StateCell(
                Key: CellName.Parse(candidate: $"k{i}"),
                Value: CellValue.Int(value: (i + 1L))
            );
        }

        StateRow[] rows = [
            new StateRow(
                Name: CellName.Parse(candidate: ProbeRow),
                Kind: CellKind.Int,
                Capacity: cells,
                Cells: probe
            ),
            new StateRow(
                Name: CellName.Parse(candidate: CursorRow),
                Kind: CellKind.Int,
                Capacity: cells,
                Cells: probe
            ),
        ];

        var section = new StateSection(Rows: rows);

        Catalog = StateCatalog.Compile(section: section);
        Arena = new StateArena(
            catalog: Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        var keys = new CellKey[cells];

        for (var i = 0; (i < cells); ++i) {
            keys[i] = Catalog.Keys.Intern(name: probe[i].Key);
        }

        ProbeKeys = keys;
        Cursor = Resolve(name: CursorRow);
        Probe = Resolve(name: ProbeRow);
    }

    public CellKey BoundEachKey { get; set; }
    public CellKey BoundPreviousKey { get; set; }
    public CellKey BoundTokenKey { get; set; }
    public Span<long> Locals => m_locals;
    public Span<long> PatternWord => m_patternWord;

    public Span<long> BoardScratch(int cells) => m_boardScratch.AsSpan(
        length: cells,
        start: 0
    );
    public int BoundIndex(BoundKey key) => -1;

    private int Resolve(string name) => (Catalog.TryResolve(
        handle: out var handle,
        lane: StateLane.Document,
        name: name
    )
        ? handle.Ordinal
        : throw new InvalidOperationException(message: $"The State kernel section does not declare row '{name}'."));
}
// A state read under a key the compiled program carries.
public sealed class StateKernelDirectOperand : RuleOperand {
    private readonly CellKey m_key;
    private readonly int m_row;

    public StateKernelDirectOperand(int row, CellKey key) : base(valueKind: CellKind.Int) {
        m_key = key;
        m_row = row;
    }

    public override RuleWork Cost(IRuleCostContext context) => 1L;
    public override RuleFact Read(IStateReader reader) => RuleFact.Finite(
        kind: CellKind.Int,
        value: ((long)RuleReads.ReadFixed(
            key: m_key,
            reader: reader,
            rowOrdinal: m_row
        ).Value)
    );
}
// A state read under a key another cell's value resolves: the read depends on a prior read's result.
public sealed class StateKernelIndirectOperand : RuleOperand {
    private readonly IReadOnlyList<CellKey> m_keys;
    private readonly CellKey m_selector;
    private readonly int m_cursor;
    private readonly int m_row;

    public StateKernelIndirectOperand(int cursor, CellKey selector, int row, IReadOnlyList<CellKey> keys) : base(valueKind: CellKind.Int) {
        m_cursor = cursor;
        m_keys = keys;
        m_row = row;
        m_selector = selector;
    }

    public override RuleWork Cost(IRuleCostContext context) => 2L;
    public override RuleFact Read(IStateReader reader) {
        var selected = ((long)RuleReads.ReadFixed(
            key: m_selector,
            reader: reader,
            rowOrdinal: m_cursor
        ).Value);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: ((long)RuleReads.ReadFixed(
                key: m_keys[((int)(((ulong)selected) % ((ulong)m_keys.Count)))],
                reader: reader,
                rowOrdinal: m_row
            ).Value)
        );
    }
}
// Builds a compiled program of exactly the requested token count. Values and operations alternate for a dependent
// chain and are segregated for independent evaluations; an even token count absorbs its parity with one unary
// operation, which leaves the stack depth unchanged.
public static class StateKernelPrograms {
    public static CompiledExpressionToken[] Build(int tokens, KernelRead read, KernelShape shape, StateKernelReader reader) {
        if (tokens <= 0) {
            return [];
        }
        var even = ((tokens % 2) == 0);
        var operations = ((tokens - (even
            ? 2
            : 1)) / 2);
        var values = (operations + 1);
        var program = new List<CompiledExpressionToken>(capacity: tokens);

        if (shape == KernelShape.IndependentEvaluations) {
            for (var i = 0; (i < values); ++i) { program.Add(item: Value(index: i, read: read, reader: reader)); }
            if (even) { program.Add(item: new(Operation: ExpressionOp.Negate)); }
            for (var i = 0; (i < operations); ++i) { program.Add(item: new(Operation: ExpressionOp.Add)); }

            return [.. program];
        }
        program.Add(item: Value(index: 0, read: read, reader: reader));
        if (even) { program.Add(item: new(Operation: ExpressionOp.Negate)); }
        for (var i = 0; (i < operations); ++i) {
            program.Add(item: Value(index: (i + 1), read: read, reader: reader));
            program.Add(item: new(Operation: ExpressionOp.Add));
        }

        return [.. program];
    }

    private static CompiledExpressionToken Value(int index, KernelRead read, StateKernelReader reader) {
        var key = reader.ProbeKeys[(index % reader.ProbeKeys.Count)];

        return read switch {
            KernelRead.Literal => new(
                Constant: (index + 1L),
                Operation: ExpressionOp.Constant
            ),
            KernelRead.Direct => new(
                Operand: new StateKernelDirectOperand(
                    key: key,
                    row: reader.Probe
                ),
                Operation: ExpressionOp.Operand
            ),
            _ => new(
                Operand: new StateKernelIndirectOperand(
                    cursor: reader.Cursor,
                    keys: reader.ProbeKeys,
                    row: reader.Probe,
                    selector: key
                ),
                Operation: ExpressionOp.Operand
            ),
        };
    }
}
