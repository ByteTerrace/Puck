namespace Puck.State;

/// <summary>One static table a section pins, as the compiler sees it: the authored name and either the loaded
/// document or the reason it could not be loaded. The loader stays with the document project; the compiler compiles
/// the document once per context on first use.</summary>
/// <param name="Name">The table's authored name.</param>
/// <param name="Document">The loaded table document, or <see langword="null"/> when <paramref name="LoadError"/>
/// says why not.</param>
/// <param name="LoadError">The load failure, or <see langword="null"/>.</param>
public readonly record struct TableSource(string Name, TableDocument? Document, string? LoadError);

/// <summary>Everything the rule compiler resolves names against, plus the per-compile scope the resolvers read as
/// they descend one rule's gate, bindings, and effects. A document project derives its own context to carry what
/// only its families resolve (a placement, a screen, a participant table) and to anchor a topology's frame.</summary>
public class RuleCompileContext {
    private readonly Dictionary<string, object> m_scope = new(comparer: StringComparer.Ordinal);
    private readonly CompiledTable?[] m_tables;
    private readonly string?[] m_tableErrors;

    /// <summary>Initializes a context over one section.</summary>
    /// <param name="section">The state section every row name resolves against.</param>
    /// <param name="catalog">The section's compiled catalog — the one every <see cref="StateHandle"/> is minted from.</param>
    /// <param name="tables">The section's pinned tables, in <c>tables</c>-row order.</param>
    /// <param name="patterns">The section's pattern rows.</param>
    /// <param name="generators">The section's generator rows.</param>
    /// <param name="simulationRateHz">The authored simulation rate, in ticks per second.</param>
    /// <param name="vocabulary">The families a name may resolve through.</param>
    public RuleCompileContext(IStateSection? section, StateCatalog catalog, IReadOnlyList<TableSource>? tables, IReadOnlyList<PatternRow>? patterns, IReadOnlyList<GeneratorRow>? generators, int simulationRateHz, RuleVocabulary vocabulary) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: vocabulary);

        Rows = (section?.Rows ?? []);
        Lattices = (section?.Lattices ?? []);
        Catalog = catalog;
        Tables = (tables ?? []);
        Patterns = (patterns ?? []);
        Generators = generators;
        SimulationRateHz = simulationRateHz;
        Vocabulary = vocabulary;
        m_tables = new CompiledTable?[Tables.Count];
        m_tableErrors = new string?[Tables.Count];
    }

    /// <summary>Gets the section's rows.</summary>
    public IReadOnlyList<StateRow> Rows { get; }
    /// <summary>Gets the section's lattice topologies.</summary>
    public IReadOnlyList<LatticeTopology> Lattices { get; }
    /// <summary>Gets the section's compiled catalog.</summary>
    public StateCatalog Catalog { get; }
    /// <summary>Gets the section's pinned tables, in <c>tables</c>-row order.</summary>
    public IReadOnlyList<TableSource> Tables { get; }
    /// <summary>Gets the section's pattern rows.</summary>
    public IReadOnlyList<PatternRow> Patterns { get; }
    /// <summary>Gets the section's generator rows.</summary>
    public IReadOnlyList<GeneratorRow>? Generators { get; }
    /// <summary>Gets the authored simulation rate, in ticks per second.</summary>
    public int SimulationRateHz { get; }
    /// <summary>Gets the families a name may resolve through.</summary>
    public RuleVocabulary Vocabulary { get; }

    /// <summary>Gets or sets the bindings the rule being compiled may name, for the duration of one compile.</summary>
    public BoundKey[]? BindingScope { get; set; }
    /// <summary>Gets or sets the rule-scoped bound values the rule being compiled has declared so far — a binding's
    /// own expression sees only the bindings declared before it; the gate and effects see all of them.</summary>
    public List<CompiledRuleBinding>? RuleBindings { get; set; }
    /// <summary>Gets or sets the enclosing rule's declared <see cref="Rule.ForEach"/> row name, for the duration of
    /// one compile.</summary>
    public string? ForEachRow { get; set; }
    /// <summary>Gets or sets the enclosing rule's compiled <see cref="Rule.Zones"/> table, which every
    /// <c>$zones[&lt;index&gt;]</c> spelling in the rule indexes, for the duration of one compile.</summary>
    public ZoneTable? Zones { get; set; }
    /// <summary>Gets the per-compile scratch a family may cache into (keyed by the family's own name), cleared with
    /// the rest of the scope when the compile ends.</summary>
    public IDictionary<string, object> Scope => m_scope;

    /// <summary>Finds a declared row by name.</summary>
    /// <param name="name">The row name.</param>
    public StateRow? FindRow(string name) => StateRows.FindStateRow(rows: Rows, name: name);

    /// <summary>Finds a discrete topology by name, compiled with no frame anchor. A document project overrides this
    /// to anchor the topology in the frame it declares.</summary>
    /// <param name="name">The topology name.</param>
    public virtual CompiledTopology? FindTopology(string name) => TopologyCompilation.Find(lattices: Lattices, name: name);

    /// <summary>Finds the draw a row redraws through on <c>generate</c>: its own <see cref="StateRow.Draw"/>. A
    /// document project overrides this to answer a row painted by a fill it declares (a lattice field's draw fill),
    /// returning an event-timed draw over that fill's source.</summary>
    /// <param name="row">The row.</param>
    public virtual Draw? FindDraw(StateRow row) => row.Draw;

    /// <summary>Returns a row's cell capacity for pricing: its authored capacity, its domain's ceiling, or the
    /// section-wide ceiling for an undeclared row.</summary>
    /// <param name="name">The row name.</param>
    public int RowCapacity(string name) {
        var row = FindRow(name: name);

        return (row?.Capacity ?? row?.CellCeiling ?? StateCapacity.MaxCellsPerRow);
    }

    /// <summary>Resolves a pinned table by name, compiling its document on first use.</summary>
    /// <param name="name">The table's authored name.</param>
    /// <param name="ordinal">The table's ordinal in the <c>tables</c> rows, or -1.</param>
    /// <param name="table">The compiled table, when it loads and compiles.</param>
    /// <param name="error">Why it did not, otherwise.</param>
    /// <returns><see langword="true"/> when the table compiled; <see langword="false"/> with <paramref name="ordinal"/>
    /// -1 when no table carries the name.</returns>
    public bool TryTable(string name, out int ordinal, out CompiledTable? table, out string? error) {
        ordinal = -1;
        table = null;
        error = null;

        for (var index = 0; index < Tables.Count; index++) {
            if (!string.Equals(a: Tables[index].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            ordinal = index;
            if ((m_tables[index] is null) && (m_tableErrors[index] is null)) {
                var source = Tables[index];
                if (source.Document is null) {
                    m_tableErrors[index] = (source.LoadError ?? "the table document did not load");
                } else if (!CompiledTable.TryCompile(name: source.Name, document: source.Document, table: out var compiled, error: out var compileError)) {
                    m_tableErrors[index] = compileError;
                } else {
                    m_tables[index] = compiled;
                }
            }

            table = m_tables[index];
            error = m_tableErrors[index];

            return (table is not null);
        }

        return false;
    }

    /// <summary>Resets the per-compile scope between rules.</summary>
    public void ClearScope() {
        BindingScope = null;
        RuleBindings = null;
        ForEachRow = null;
        Zones = null;
        m_scope.Clear();
    }
}
