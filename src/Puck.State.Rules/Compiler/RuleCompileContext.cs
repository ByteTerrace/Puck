namespace Puck.State.Rules;

/// <summary>One static table a section pins, as the compiler sees it: the authored name and either the loaded
/// document or the reason it could not be loaded.</summary>
/// <param name="Name">The table's authored name.</param>
/// <param name="Document">The loaded table document, or <see langword="null"/> when <paramref name="LoadError"/>
/// says why not.</param>
/// <param name="LoadError">The load failure, or <see langword="null"/>.</param>
public readonly record struct TableSource(string Name, TableDocument? Document, string? LoadError);
/// <summary>Everything the rule compiler resolves names against, plus the per-compile scope the resolvers read as
/// they descend one rule's gate, bindings, and effects. A document project derives its own context to carry what
/// only its families resolve and to anchor a topology's frame.</summary>
/// <remarks>A name reaches an ordinal exactly here. Every compiled address the walk produces is a
/// <c>(row ordinal, cell key)</c> pair, so the compiled program the evaluator reads carries no authored name
/// outside a read-back spelling.</remarks>
public class RuleCompileContext : IRuleCostContext {
    private readonly Dictionary<string, CompiledPattern> m_patternsByName = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, object> m_scope = new(comparer: StringComparer.Ordinal);

    private readonly string?[] m_tableErrors;
    private readonly CompiledTable?[] m_tables;

    /// <summary>Initializes a context over one section.</summary>
    /// <param name="section">The state section every row name resolves against.</param>
    /// <param name="catalog">The section's compiled catalog — the one every ordinal and key is minted from.</param>
    /// <param name="tables">The section's pinned tables, in <c>tables</c>-row order.</param>
    /// <param name="patterns">The section's pattern rows.</param>
    /// <param name="generators">The section's generator rows.</param>
    /// <param name="simulationRateHz">The authored simulation rate, in ticks per second.</param>
    /// <param name="vocabulary">The families a name may resolve through.</param>
    public RuleCompileContext(IStateSection? section, StateCatalog catalog, IReadOnlyList<TableSource>? tables, IReadOnlyList<PatternRow>? patterns, IReadOnlyList<GeneratorRow>? generators, int simulationRateHz, RuleVocabulary vocabulary) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: vocabulary);

        Catalog = catalog;
        Generators = generators;
        Lattices = (section?.Lattices ?? []);
        Patterns = (patterns ?? []);
        Rows = (section?.Rows ?? []);
        SimulationRateHz = simulationRateHz;
        Spaces = (section?.Spaces ?? []);
        Tables = (tables ?? []);
        Vocabulary = vocabulary;
        m_tableErrors = new string?[Tables.Count];
        m_tables = new CompiledTable?[Tables.Count];
    }

    /// <summary>Gets or sets the bindings the rule being compiled may name, for the duration of one compile.</summary>
    public BoundKey[]? BindingScope { get; set; }
    /// <summary>Gets the section's compiled catalog.</summary>
    public StateCatalog Catalog { get; }
    /// <summary>Gets or sets the enclosing rule's declared <see cref="Rule.ForEach"/> row name, for the duration of
    /// one compile.</summary>
    public string? ForEachRow { get; set; }
    /// <summary>Gets the section's generator rows.</summary>
    public IReadOnlyList<GeneratorRow>? Generators { get; }
    /// <summary>Gets the section's lattice topologies.</summary>
    public IReadOnlyList<LatticeTopology> Lattices { get; }

    /// <summary>Gets the needs the rule being compiled accumulates, reset between rules by
    /// <see cref="ClearScope"/>.</summary>
    public RuleNeedsBuilder Needs { get; } = new();

    /// <summary>Gets the section's pattern rows.</summary>
    public IReadOnlyList<PatternRow> Patterns { get; }

    /// <summary>Gets the document's declared cell sets, keyed by the names a rule addresses them with. A document
    /// project that carries a `sets` member assigns this when it builds the context.</summary>
    public IReadOnlyList<CellSetRow> Sets { get; init; } = [];

    /// <summary>Gets the section's rows.</summary>
    public IReadOnlyList<StateRow> Rows { get; }
    /// <summary>Gets or sets the rule-scoped bound values the rule being compiled has declared so far — a binding's
    /// own expression sees only the bindings declared before it; the gate and effects see all of them.</summary>
    public List<CompiledRuleLocal>? RuleLocals { get; set; }
    /// <summary>Gets the per-compile scratch a family may cache into, cleared when the compile ends.</summary>
    public IDictionary<string, object> Scope => m_scope;
    /// <summary>Gets the authored simulation rate, in ticks per second.</summary>
    public int SimulationRateHz { get; }
    /// <summary>Gets the section's vector embedding spaces.</summary>
    public IReadOnlyList<StateSpace> Spaces { get; }
    /// <summary>Gets the section's pinned tables, in <c>tables</c>-row order.</summary>
    public IReadOnlyList<TableSource> Tables { get; }
    /// <summary>Gets the families a name may resolve through.</summary>
    public RuleVocabulary Vocabulary { get; }
    /// <summary>Gets or sets the enclosing rule's compiled <see cref="Rule.Zones"/> table, which every
    /// <c>$zones[&lt;index&gt;]</c> spelling in the rule indexes, for the duration of one compile.</summary>
    public ZoneTable? Zones { get; set; }

    // Scope identity keeps pattern-local token bindings separate from the enclosing rule's bindings.
    internal Dictionary<(string Text, BoundKey[]? Scope), CompiledCellRef> KeyExpressions { get; } = [];

    /// <summary>Resets the per-compile scope between rules.</summary>
    public void ClearScope() {
        BindingScope = null;
        ForEachRow = null;
        RuleLocals = null;
        Zones = null;

        KeyExpressions.Clear();
        Needs.Clear();
        m_scope.Clear();
    }
    /// <summary>Finds the draw a row redraws through on <c>generate</c>: its own <see cref="StateRow.Draw"/>. A
    /// document project overrides this to answer a row painted by a fill it declares.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The draw, or <see langword="null"/>.</returns>
    public virtual Draw? FindDraw(StateRow row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        return row.Draw;
    }
    /// <summary>Finds a declared row by name.</summary>
    /// <param name="name">The row name.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    public StateRow? FindRow(string name) => StateRows.FindStateRow(
        name: name,
        rows: Rows
    );
    /// <summary>Finds a declared row by its catalog ordinal.</summary>
    /// <param name="rowOrdinal">The catalog ordinal.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    public StateRow? FindRowAt(int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)Catalog.Count)) {
            return null;
        }

        var descriptor = Catalog.Descriptors[rowOrdinal];

        return ((descriptor.Lane == StateLane.Document)
            ? FindRow(name: descriptor.Name)
            : null
        );
    }
    /// <summary>Finds a declared vector space by name, or the default space when exactly one space exists and the
    /// name is absent.</summary>
    /// <param name="name">The space name, or <see langword="null"/> for the default space.</param>
    /// <returns>The space, or <see langword="null"/>.</returns>
    public StateSpace? FindSpace(string? name = null) {
        if (string.IsNullOrEmpty(value: name)) {
            return ((Spaces.Count == 1)
                ? Spaces[0]
                : null
            );
        }

        foreach (var space in Spaces) {
            if (string.Equals(
                a: space.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return space;
            }
        }

        return null;
    }
    /// <summary>Finds a discrete topology by name, compiled with no frame anchor. A document project overrides this
    /// to anchor the topology in the frame it declares.</summary>
    /// <param name="name">The topology name.</param>
    /// <returns>The compiled topology, or <see langword="null"/>.</returns>
    public virtual CompiledTopology? FindTopology(string name) => TopologyCompilation.Find(
        lattices: Lattices,
        name: name
    );
    /// <summary>Returns a row's cell capacity for pricing: its authored capacity, its domain's ceiling, or the
    /// section-wide ceiling for an undeclared row.</summary>
    /// <param name="name">The row name.</param>
    /// <returns>The capacity.</returns>
    public long RowCapacity(string name) {
        var row = FindRow(name: name);

        return (row?.Capacity ?? (row?.CellCeiling ?? StateCapacity.MaxCellsPerRow));
    }
    /// <inheritdoc/>
    public long RowCapacity(int rowOrdinal) {
        var row = FindRowAt(rowOrdinal: rowOrdinal);

        return (row?.Capacity ?? (row?.CellCeiling ?? StateCapacity.MaxCellsPerRow));
    }
    /// <summary>Resolves a declared cell set by name.</summary>
    /// <param name="name">The set's name.</param>
    /// <param name="set">The declared expression, on success.</param>
    /// <returns><see langword="true"/> when the document declares a set of that name.</returns>
    public bool TryCellSet(string name, out CellSetExpression? set) {
        foreach (var row in Sets) {
            if ((row is not null) && string.Equals(a: row.Name.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                set = row.Set;

                return true;
            }
        }

        set = null;

        return false;
    }
    /// <summary>Resolves a declared pattern row by name, compiling it on first use.</summary>
    /// <param name="name">The pattern's authored name.</param>
    /// <param name="pattern">The compiled pattern, on success.</param>
    /// <param name="reason">Why it did not compile, otherwise.</param>
    /// <returns><see langword="true"/> when the pattern compiled.</returns>
    public bool TryPattern(string name, out CompiledPattern? pattern, out string reason) {
        if (m_patternsByName.TryGetValue(
            key: name,
            value: out var cached
        )) {
            pattern = cached;
            reason = string.Empty;

            return true;
        }

        foreach (var candidate in Patterns) {
            if (
                (candidate is null) ||
                !string.Equals(
                a: candidate.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }
            if (!CompiledPattern.TryCompile(
                compiled: out var compiled,
                reason: out reason,
                row: candidate
            )) {
                pattern = null;

                return false;
            }

            m_patternsByName[name] = compiled!;
            pattern = compiled;

            return true;
        }

        pattern = null;
        reason = $"'{name}' names no pattern";

        return false;
    }
    /// <summary>Resolves a pinned table by name, compiling its document on first use.</summary>
    /// <param name="name">The table's authored name.</param>
    /// <param name="ordinal">The table's ordinal in the <c>tables</c> rows, or <c>-1</c>.</param>
    /// <param name="table">The compiled table, when it loads and compiles.</param>
    /// <param name="error">Why it did not, otherwise.</param>
    /// <returns><see langword="true"/> when the table compiled.</returns>
    public bool TryTable(string name, out int ordinal, out CompiledTable? table, out string? error) {
        ordinal = -1;
        table = null;
        error = null;

        for (var index = 0; (index < Tables.Count); index++) {
            if (!string.Equals(
                a: Tables[index].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            ordinal = index;
            if (
                (m_tables[index] is null) &&
                (m_tableErrors[index] is null)
            ) {
                var source = Tables[index];

                if (source.Document is null) {
                    m_tableErrors[index] = (source.LoadError ?? "the table document did not load");
                } else if (!CompiledTable.TryCompile(
                    document: source.Document,
                    error: out var compileError,
                    name: source.Name,
                    table: out var compiled
                )) {
                    m_tableErrors[index] = compileError;
                } else {
                    m_tables[index] = compiled;
                }
            }

            error = m_tableErrors[index];
            table = m_tables[index];

            return (table is not null);
        }

        return false;
    }
}
