namespace Puck.State;

/// <summary>A rule's <see cref="Rule.Zones"/> table as the evaluator indexes it: ordered zones over one token domain,
/// each entry's compiled handle in index order, with an invalid handle at every gap. Compiled once per rule by
/// <see cref="RuleCompiler.CompileZones"/>; every <see cref="LiveZone"/> the rule spells indexes this one table and
/// is listed in <see cref="References"/>, and a rule iterating <c>forEach: "$zones"</c> visits <see cref="Indices"/>
/// in order.</summary>
public sealed class ZoneTable {
    private readonly StateHandle[] m_handles;
    private readonly List<LiveZone> m_references = [];

    /// <param name="names">The authored entries, in index order; an empty entry is a gap.</param>
    /// <param name="handles">Each entry's compiled handle; invalid at a gap.</param>
    /// <param name="tokenDomain">The token domain every zone in the table is ordered over.</param>
    /// <param name="kind">The cell kind every zone in the table shares.</param>
    /// <param name="capacity">The widest entry's cell capacity, for pricing a live read.</param>
    /// <param name="indices">The non-gap indices spelled as cell keys, for a <c>forEach</c> over the table.</param>
    public ZoneTable(IReadOnlyList<string> names, StateHandle[] handles, string tokenDomain, CellKind kind, int capacity, CellName[] indices) {
        ArgumentNullException.ThrowIfNull(argument: names);
        ArgumentNullException.ThrowIfNull(argument: handles);
        ArgumentNullException.ThrowIfNull(argument: indices);

        Names = names;
        m_handles = handles;
        TokenDomain = tokenDomain;
        Kind = kind;
        Capacity = capacity;
        Indices = indices;
    }

    /// <summary>Gets the authored entries, in index order; an empty entry is a gap.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>Gets the token domain every zone in the table is ordered over.</summary>
    public string TokenDomain { get; }
    /// <summary>Gets the cell kind every zone in the table shares.</summary>
    public CellKind Kind { get; }
    /// <summary>Gets the widest entry's cell capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the non-gap indices spelled as cell keys, in index order.</summary>
    public CellName[] Indices { get; }
    /// <summary>Gets every live reference the rule spells against this table, one per distinct spelling, in the
    /// order they were compiled. An evaluation applies only when every one of them selects a zone.</summary>
    public IReadOnlyList<LiveZone> References => m_references;

    /// <summary>Returns the live reference a spelling names, minting it on first use — the same spelling in two
    /// places is one reference, resolved once per evaluation.</summary>
    /// <param name="index">The compiled index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    public LiveZone Reference(CompiledCellRef index, string spelling) {
        foreach (var existing in m_references) {
            if (string.Equals(a: existing.Spelling, b: spelling, comparisonType: StringComparison.Ordinal)) {
                return existing;
            }
        }
        var minted = new LiveZone(table: this, index: index, spelling: spelling);
        m_references.Add(item: minted);
        return minted;
    }

    /// <summary>Resolves an index to its zone's handle; an index outside the table or at a gap answers none.</summary>
    /// <param name="index">The live index.</param>
    /// <param name="handle">The zone's compiled handle, on success.</param>
    public bool TryHandle(long index, out StateHandle handle) {
        if (((ulong)index) < ((ulong)m_handles.Length) && m_handles[index].IsValid) {
            handle = m_handles[index];

            return true;
        }

        handle = default;

        return false;
    }

    /// <summary>Returns the zone name an index selects, or <see langword="null"/> outside the table or at a gap.</summary>
    /// <param name="index">The live index.</param>
    public string? NameAt(long index) => ((((ulong)index) < ((ulong)m_handles.Length) && (Names[(int)index].Length != 0)) ? Names[(int)index] : null);

    /// <summary>Appends every zone the table can select, as a whole-row access.</summary>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access is a write.</param>
    public void CollectRows(List<RuleAccess> into, bool isSet) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var name in Names) {
            if (name.Length != 0) {
                into.Add(item: new RuleAccess(Row: name, Key: null, IsSet: isSet));
            }
        }
    }
}

/// <summary>A row position chosen live — <c>$zones[&lt;index&gt;]</c>, the row-side mirror of a key indirection: the
/// bracketed index (a cell read, a bound token, a binding, or an expression) resolves before each read or firing to
/// an integer that selects a zone from the enclosing rule's <see cref="ZoneTable"/>. An index outside the table or at
/// a gap selects no zone, and the rule's evaluation does not apply: the evaluator proves every reference selected
/// before the gate, so an unselected index closes the gate rather than refusing an effect. Outside an evaluation
/// (a read-back, a search score) a read through an unselected reference is the absent fact and a transfer end
/// refuses by name.</summary>
public sealed class LiveZone {
    /// <param name="table">The rule's zone table.</param>
    /// <param name="index">The live index indirection.</param>
    /// <param name="spelling">The authored spelling, for read-backs and refusals.</param>
    public LiveZone(ZoneTable table, CompiledCellRef index, string spelling) {
        ArgumentNullException.ThrowIfNull(argument: table);

        Table = table;
        Index = index;
        Spelling = spelling;
    }

    /// <summary>Gets the rule's zone table.</summary>
    public ZoneTable Table { get; }
    /// <summary>Gets the live index indirection.</summary>
    public CompiledCellRef Index { get; }
    /// <summary>Gets the authored spelling.</summary>
    public string Spelling { get; }
    /// <summary>Gets a value indicating whether the index needs the document host.</summary>
    public bool HostOnly => (Index.Custom is { HostOnly: true });

    private bool TryResolveIndex(IRuleReader reader, out long index) {
        var reference = Index;

        return RuleEvaluation.TryResolveIndex(reader: reader, reference: in reference, index: out index);
    }

    /// <summary>Resolves the zone for the evaluation in flight.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="handle">The selected zone's handle, on success.</param>
    public bool TryResolve(IRuleReader reader, out StateHandle handle) {
        if (TryResolveIndex(reader: reader, index: out var index)) {
            return Table.TryHandle(index: index, handle: out handle);
        }

        handle = default;

        return false;
    }

    /// <summary>Resolves the zone's name for the evaluation in flight — or, when the index selects none, the authored
    /// spelling, which no row carries, so a mutation naming it refuses.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    public string ResolveName(IRuleReader reader) =>
        ((TryResolveIndex(reader: reader, index: out var index) ? Table.NameAt(index: index) : null) ?? Spelling);

    /// <summary>Appends the cells the index resolves through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectIndexReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: Index, into: into);

    /// <summary>Appends every zone a read through this reference can touch, plus the cells the index resolves through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectReads(List<RuleAccess> into) {
        Table.CollectRows(into: into, isSet: false);
        CollectIndexReads(into: into);
    }
}
