using Xunit;

namespace Puck.State.Tests;

public sealed class TextKeyIndirectionTests {
    private sealed class TestRuleReader : IRuleReader {
        public required StateStore Store { get; init; }
        public required StateCatalog Catalog { get; init; }
        public string? BoundEachKey => null;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
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

    private static StateHandle DocumentRow(StateCatalog catalog, string name) =>
        catalog.TryResolve(StateLane.Document, name, out var handle) ? handle : default;

    [Fact]
    public void TextCell_DynamicKeyIndirection_ResolvesValidKey() {
        var textRow = new StateRow(
            Name: CellName.Parse("textTable"),
            Kind: CellKind.Text,
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("pointer"), Text: "targetCell")]
        );

        var intRow = new StateRow(
            Name: CellName.Parse("intTable"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("targetCell"), Value: 42L)]
        );

        var section = new StateSection(Spaces: [], Rows: [textRow, intRow]);
        var catalog = StateCatalog.Compile(section: section);
        var store = new RowStore(rows: [textRow, intRow]);
        var reader = new TestRuleReader { Store = store, Catalog = catalog };

        var cellRef = new CompiledCellRef(
            Handle: DocumentRow(catalog, name: "textTable"),
            InnerKeyBinding: BoundKey.None,
            Key: "pointer",
            Row: "textTable",
            Kind: CellKind.Text,
            CellKey: CellName.Parse("pointer")
        );

        var resolvedKey = RuleEvaluation.ResolveKey(reader: reader, key: null, keyFrom: cellRef);
        Assert.Equal("targetCell", resolvedKey);
    }

    [Fact]
    public void TextCell_DynamicKeyIndirection_InvalidCellName_ThrowsKeyIndirectionInvalid() {
        var textRow = new StateRow(
            Name: CellName.Parse("textTable"),
            Kind: CellKind.Text,
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("badPointer"), Text: "bad.key")]
        );

        var section = new StateSection(Spaces: [], Rows: [textRow]);
        var catalog = StateCatalog.Compile(section: section);
        var store = new RowStore(rows: [textRow]);
        var reader = new TestRuleReader { Store = store, Catalog = catalog };

        var cellRef = new CompiledCellRef(
            Handle: DocumentRow(catalog, name: "textTable"),
            InnerKeyBinding: BoundKey.None,
            Key: "badPointer",
            Row: "textTable",
            Kind: CellKind.Text,
            CellKey: CellName.Parse("badPointer")
        );

        var ex = Assert.Throws<RuleException>(() =>
            RuleEvaluation.ResolveKey(reader: reader, key: null, keyFrom: cellRef)
        );
        Assert.Equal(RuleRefusal.KeyIndirectionInvalid, ex.Refusal);
    }

    [Fact]
    public void TextCell_DynamicKeyIndirection_EmptyOrAbsent_ReturnsEmpty() {
        var textRow = new StateRow(
            Name: CellName.Parse("textTable"),
            Kind: CellKind.Text,
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("emptyPointer"), Text: "")]
        );

        var section = new StateSection(Spaces: [], Rows: [textRow]);
        var catalog = StateCatalog.Compile(section: section);
        var store = new RowStore(rows: [textRow]);
        var reader = new TestRuleReader { Store = store, Catalog = catalog };

        var cellRef = new CompiledCellRef(
            Handle: DocumentRow(catalog, name: "textTable"),
            InnerKeyBinding: BoundKey.None,
            Key: "emptyPointer",
            Row: "textTable",
            Kind: CellKind.Text,
            CellKey: CellName.Parse("emptyPointer")
        );

        var resolvedKey = RuleEvaluation.ResolveKey(reader: reader, key: null, keyFrom: cellRef);
        Assert.Equal(string.Empty, resolvedKey);

        // Absent cell
        var absentCellRef = new CompiledCellRef(
            Handle: DocumentRow(catalog, name: "textTable"),
            InnerKeyBinding: BoundKey.None,
            Key: "nonExistentPointer",
            Row: "textTable",
            Kind: CellKind.Text,
            CellKey: CellName.Parse("nonExistentPointer")
        );

        var absentKey = RuleEvaluation.ResolveKey(reader: reader, key: null, keyFrom: absentCellRef);
        Assert.Equal(string.Empty, absentKey);
    }
}
