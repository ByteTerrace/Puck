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
    private readonly List<RuleInstanceBinding> m_instanceBindings = [];
    private readonly HashSet<int> m_releasedInstanceSlots = [];

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
        Section = section;
        Generators = generators;
        Lattices = (section?.Lattices ?? []);
        Patterns = (patterns ?? []);
        // The catalog gives record fields and pool bookkeeping stable row ordinals by expanding them into the
        // section's row universe. Keep the compiler's metadata view over that same universe: resolving a generated
        // field by ordinal and then pricing it against only the authored rows would silently fall back to the
        // section-wide ceiling.
        Rows = StateCatalog.ExpandRows(section: section);
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
    /// <summary>Gets the authored section used for layout and retained-journal admission.</summary>
    public IStateSection? Section { get; }
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

    /// <summary>Resolves a lexical pool-instance binding visible at the current compilation point.</summary>
    public bool TryInstanceBinding(string name, out RuleInstanceBinding binding) {
        for (var index = (m_instanceBindings.Count - 1); (index >= 0); index--) {
            if (string.Equals(a: m_instanceBindings[index].Name, b: name, comparisonType: StringComparison.Ordinal) && !m_releasedInstanceSlots.Contains(item: m_instanceBindings[index].Slot)) {
                binding = m_instanceBindings[index];
                return true;
            }
        }
        binding = default;
        return false;
    }
    /// <summary>Resolves a declared ordinary pool slot for direct logical field access. The arena binds that slot to
    /// its currently live lifetime at evaluation time.</summary>
    public bool TryStaticPoolHandle(string poolName, int slot, out StatePoolDescriptor? pool, out StateInstanceHandle handle) {
        if (Catalog.TryGetPool(name: CellName.Parse(candidate: poolName), pool: out pool) && (pool is not null) && !pool.IsPair && (((uint)slot) < ((uint)pool.Capacity))) {
            handle = Catalog.CreateInstanceHandle(poolOrdinal: pool.Ordinal, slot: slot, generation: 0L);
            return true;
        }
        pool = null;
        handle = default;
        return false;
    }
    /// <summary>Opens one lexical instance binding and returns its distinct evaluator register.</summary>
    /// <param name="name">The authored binding name.</param>
    /// <param name="pool">The pool the binding belongs to.</param>
    /// <param name="effectScoped"><see langword="true"/> for a binding an effect body opens (a claim, a pair claim,
    /// or a <c>for each</c> effect), whose handle exists only while that body fires; <see langword="false"/> for a
    /// binding bound before the rule's locals and gate evaluate.</param>
    /// <returns>The binding.</returns>
    public RuleInstanceBinding PushInstanceBinding(CellName name, StatePoolDescriptor pool, bool effectScoped = false) {
        ArgumentNullException.ThrowIfNull(argument: pool);
        if (m_instanceBindings.Count >= StateCapacity.MaxInstanceBindings) {
            throw new InvalidOperationException(message: $"a rule may bind at most {StateCapacity.MaxInstanceBindings} pool instances");
        }
        foreach (var existing in m_instanceBindings) {
            if (string.Equals(a: existing.Name, b: name.Value, comparisonType: StringComparison.Ordinal)) {
                throw new InvalidOperationException(message: $"instance binding '{name}' is already live in this scope");
            }
        }
        var binding = new RuleInstanceBinding(Name: name.Value, Pool: pool, Slot: m_instanceBindings.Count, EffectScoped: effectScoped);

        m_instanceBindings.Add(item: binding);
        _ = m_releasedInstanceSlots.Remove(item: binding.Slot);
        return binding;
    }
    /// <summary>Determines whether an evaluator register belongs to a binding an effect body opened, whose handle
    /// exists only while that body fires.</summary>
    /// <param name="slot">The binding's evaluator register.</param>
    /// <returns><see langword="true"/> when an effect-scoped binding in scope owns the register.</returns>
    public bool IsEffectScopedBinding(int slot) {
        foreach (var binding in m_instanceBindings) {
            if ((binding.Slot == slot) && binding.EffectScoped) {
                return true;
            }
        }
        return false;
    }
    /// <summary>Marks a lexical binding unavailable after its release effect.</summary>
    public void ReleaseInstanceBinding(RuleInstanceBinding binding) => m_releasedInstanceSlots.Add(item: binding.Slot);
    /// <summary>Closes the most recently opened lexical instance binding.</summary>
    public void PopInstanceBinding(RuleInstanceBinding binding) {
        if ((m_instanceBindings.Count == 0) || !EqualityComparer<RuleInstanceBinding>.Default.Equals(x: m_instanceBindings[^1], y: binding)) {
            throw new InvalidOperationException(message: "pool instance scopes must close in lexical order");
        }
        m_instanceBindings.RemoveAt(index: (m_instanceBindings.Count - 1));
        _ = m_releasedInstanceSlots.Remove(item: binding.Slot);
    }

    // Scope identity keeps pattern-local token bindings separate from the enclosing rule's bindings.
    internal Dictionary<(string Text, BoundKey[]? Scope), CompiledCellRef> KeyExpressions { get; } = [];

    /// <summary>Resets the per-compile scope between rules.</summary>
    public void ClearScope() {
        BindingScope = null;
        ForEachRow = null;
        m_instanceBindings.Clear();
        m_releasedInstanceSlots.Clear();
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
    /// <summary>Finds an authored row by name. Generated storage rows are never addressable by an authored name.</summary>
    /// <remarks>Pool field compilation and costing use <see cref="FindRowAt"/> after resolving a pool handle.</remarks>
    /// <param name="name">The row name.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    public StateRow? FindRow(string name) => StateRows.FindStateRow(name: name, rows: Rows);
    /// <summary>Finds a declared or generated storage row by its catalog ordinal.</summary>
    /// <param name="rowOrdinal">The catalog ordinal.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    public StateRow? FindRowAt(int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)Catalog.Count)) {
            return null;
        }

        var descriptor = Catalog.Descriptors[rowOrdinal];

        return (((descriptor.Lane == StateLane.Document) && (((uint)descriptor.LaneOrdinal) < ((uint)Rows.Count)))
            ? Rows[descriptor.LaneOrdinal]
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
    /// <param name="set">The declared set, its name beside its expression, on success.</param>
    /// <returns><see langword="true"/> when the document declares a set of that name.</returns>
    public bool TryCellSet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CellSetRow? set) {
        foreach (var row in Sets) {
            if ((row?.Set is not null) && string.Equals(a: row.Name.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                set = row;

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
/// <summary>One typed lexical instance binding and its evaluator register slot.</summary>
/// <param name="Name">The authored binding name.</param>
/// <param name="Pool">The pool the binding belongs to.</param>
/// <param name="Slot">The distinct evaluator register slot.</param>
/// <param name="EffectScoped"><see langword="true"/> when an effect body opened the binding, so its handle exists only
/// while that body fires.</param>
public readonly record struct RuleInstanceBinding(string Name, StatePoolDescriptor Pool, int Slot, bool EffectScoped = false);
