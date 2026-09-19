namespace Puck.State.Rules;

/// <summary>A rule's <see cref="Rule.Zones"/> table as the evaluator indexes it: ordered zones over one token
/// domain, each entry's catalog ordinal in index order, with <c>-1</c> at every gap.</summary>
public sealed class ZoneTable {
    private readonly int[] m_ordinals;
    private readonly List<LiveRow> m_references = [];

    /// <summary>Initializes the table.</summary>
    /// <param name="names">The authored entries, in index order; an empty entry is a gap.</param>
    /// <param name="ordinals">Each entry's catalog ordinal; <c>-1</c> at a gap.</param>
    /// <param name="tokenDomain">The token domain every zone in the table is ordered over.</param>
    /// <param name="kind">The cell kind every zone in the table shares.</param>
    /// <param name="capacity">The widest entry's cell capacity, for pricing a live read.</param>
    /// <param name="indices">The non-gap indices spelled as interned cell keys, for a <c>forEach</c> over the table.</param>
    public ZoneTable(IReadOnlyList<string> names, int[] ordinals, string tokenDomain, CellKind kind, int capacity, CellKey[] indices) {
        ArgumentNullException.ThrowIfNull(argument: names);
        ArgumentNullException.ThrowIfNull(argument: ordinals);
        ArgumentNullException.ThrowIfNull(argument: indices);

        Capacity = capacity;
        Indices = indices;
        Kind = kind;
        Names = names;
        TokenDomain = tokenDomain;
        m_ordinals = ordinals;
    }

    /// <summary>Gets the widest entry's cell capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the non-gap indices spelled as interned cell keys, in index order.</summary>
    public CellKey[] Indices { get; }
    /// <summary>Gets the cell kind every zone in the table shares.</summary>
    public CellKind Kind { get; }
    /// <summary>Gets the authored entries, in index order; an empty entry is a gap. Read-back only.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>Gets the catalog ordinals of every entry, in index order; <c>-1</c> at a gap.</summary>
    public IReadOnlyList<int> Ordinals => m_ordinals;
    /// <summary>Gets every live reference the rule spells against this table, one per distinct spelling, in the
    /// order they were compiled. An evaluation applies only when every one of them selects a row.</summary>
    public IReadOnlyList<LiveRow> References => m_references;
    /// <summary>Gets the token domain every zone in the table is ordered over.</summary>
    public string TokenDomain { get; }

    /// <summary>Appends every zone the table can select, as a whole-row access.</summary>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access is a write.</param>
    public void CollectRows(List<CellAccess> into, bool isSet) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var ordinal in m_ordinals) {
            if (ordinal >= 0) {
                into.Add(item: new CellAccess(
                    IsSet: isSet,
                    Key: default,
                    RowOrdinal: ordinal
                ));
            }
        }
    }
    /// <summary>Returns the live reference a spelling names, minting it on first use — the same spelling in two
    /// places is one reference, resolved once per evaluation.</summary>
    /// <param name="index">The compiled index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <returns>The reference.</returns>
    public LiveRow Reference(CompiledCellRef index, string spelling) {
        foreach (var existing in m_references) {
            if (string.Equals(
                a: existing.Spelling,
                b: spelling,
                comparisonType: StringComparison.Ordinal
            )) {
                return existing;
            }
        }

        var minted = LiveRow.OverZones(
            index: index,
            spelling: spelling,
            table: this
        );

        m_references.Add(item: minted);

        return minted;
    }
    /// <summary>Resolves an index to its zone's catalog ordinal; an index outside the table or at a gap answers
    /// none.</summary>
    /// <param name="index">The live index.</param>
    /// <param name="rowOrdinal">The selected zone's catalog ordinal, on success.</param>
    /// <returns><see langword="true"/> when the index selects a zone.</returns>
    public bool TryOrdinal(long index, out int rowOrdinal) {
        if (
            (((ulong)index) < ((ulong)m_ordinals.Length)) &&
            (m_ordinals[index] >= 0)
        ) {
            rowOrdinal = m_ordinals[index];

            return true;
        }

        rowOrdinal = -1;

        return false;
    }
}
/// <summary>A row position chosen live: <c>$zones[&lt;index&gt;]</c> against the enclosing rule's
/// <see cref="ZoneTable"/>, or <c>&lt;family&gt;[&lt;index&gt;]</c> against a declared <see cref="RowFamily"/>. The
/// bracketed index resolves before each read or firing to an integer that selects a row; an index outside the table
/// or family selects none, and the rule's evaluation is not for it.</summary>
public sealed class LiveRow {
    private readonly RowFamily m_family;

    private LiveRow(ZoneTable? table, RowFamily family, CompiledCellRef index, string spelling) {
        Index = index;
        Spelling = spelling;
        Table = table;
        m_family = family;
    }

    /// <summary>Gets the family the index selects a member of, or <see langword="null"/> for a zone table.</summary>
    public RowFamily? Family => ((Table is null)
        ? m_family
        : null
    );
    /// <summary>Gets the live index indirection.</summary>
    public CompiledCellRef Index { get; }
    /// <summary>Gets the widest row this reference can select, for pricing a live read.</summary>
    public int SelectionCapacity => ((Table is { } table)
        ? table.Capacity
        : StateCapacity.MaxCellsPerRow
    );
    /// <summary>Gets the authored spelling.</summary>
    public string Spelling { get; }
    /// <summary>Gets the rule's zone table, or <see langword="null"/> for a family selection.</summary>
    public ZoneTable? Table { get; }

    /// <summary>Creates a family-member selection.</summary>
    /// <param name="family">The declared family.</param>
    /// <param name="index">The live index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <returns>The reference.</returns>
    public static LiveRow OverFamily(RowFamily family, CompiledCellRef index, string spelling) => new(
        family: family,
        index: index,
        spelling: spelling,
        table: null
    );
    /// <summary>Creates a zone-table selection.</summary>
    /// <param name="table">The rule's zone table.</param>
    /// <param name="index">The live index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <returns>The reference.</returns>
    public static LiveRow OverZones(ZoneTable table, CompiledCellRef index, string spelling) {
        ArgumentNullException.ThrowIfNull(argument: table);

        return new LiveRow(
            family: default,
            index: index,
            spelling: spelling,
            table: table
        );
    }
    /// <summary>Appends the cells the index resolves through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectIndexReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: Index
    );
    /// <summary>Appends every row a read through this reference can touch, plus the cells the index resolves
    /// through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (Table is { } table) {
            table.CollectRows(
                into: into,
                isSet: false
            );
        } else {
            foreach (var ordinal in m_family.Ordinals()) {
                into.Add(item: new CellAccess(
                    Key: default,
                    RowOrdinal: ordinal
                ));
            }
        }

        CollectIndexReads(into: into);
    }
    /// <summary>Appends every row this reference can select, without the cells its index resolves through.</summary>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access replaces the row's contents.</param>
    public void CollectRows(List<CellAccess> into, bool isSet) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (Table is { } table) {
            table.CollectRows(
                into: into,
                isSet: isSet
            );

            return;
        }

        foreach (var ordinal in m_family.Ordinals()) {
            into.Add(item: new CellAccess(
                IsSet: isSet,
                Key: default,
                RowOrdinal: ordinal
            ));
        }
    }
    /// <summary>Appends every fact the index carries, so a rule's needs fold the whole reference.</summary>
    /// <param name="into">The needs being built.</param>
    public void CollectFacts(RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.AddFact(fact: Index.Custom);
    }
    /// <summary>Resolves the row for the evaluation in flight.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The selected row's catalog ordinal, on success.</param>
    /// <returns><see langword="true"/> when the index selects a row.</returns>
    public bool TryResolve(IStateReader reader, out int rowOrdinal) {
        var reference = Index;

        if (!RuleReads.TryResolveIndex(
            index: out var index,
            reader: reader,
            reference: in reference
        )) {
            rowOrdinal = -1;

            return false;
        }
        if (Table is { } table) {
            return table.TryOrdinal(
                index: index,
                rowOrdinal: out rowOrdinal
            );
        }

        return m_family.TryGetOrdinal(
            index: ((int)Math.Clamp(
                max: int.MaxValue,
                min: int.MinValue,
                value: index
            )),
            ordinal: out rowOrdinal
        );
    }
}
/// <summary>The key an implicit local computed: the cell whose name is the local's integer.</summary>
public sealed class LocalKeyFact : RuleKeyFact {
    /// <summary>Initializes the fact.</summary>
    /// <param name="ordinal">The local's slot in the rule.</param>
    /// <param name="source">The local's compiled expression, whose reads this key rests on.</param>
    public LocalKeyFact(int ordinal, CompiledExpressionToken[]? source) {
        Ordinal = ordinal;
        Source = source;
    }

    /// <summary>Gets the local's slot in the rule.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the local's compiled expression.</summary>
    public CompiledExpressionToken[]? Source { get; }

    /// <summary>Appends what the local's expression reads: a read addressed by this key moves whenever they do, so a
    /// schedule or a memo built over the read rests on them too.</summary>
    /// <param name="into">The read set being collected.</param>
    public override void CollectReads(List<CellAccess> into) => RuleDataflow.CollectExpression(
        into: into,
        tokens: Source
    );
    /// <inheritdoc/>
    public override CellKey Resolve(IStateReader reader, out bool named) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        // A binding spells an integer, so it always names a cell; one no key table interns is a cell no row holds.
        named = true;

        return (RuleReads.TryIndexKey(
            catalog: reader.Catalog,
            index: reader.LocalValue(ordinal: Ordinal),
            key: out var key
        )
            ? key
            : default
        );
    }
    /// <inheritdoc/>
    public override bool TryResolveIndex(IStateReader reader, out long index) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        index = reader.LocalValue(ordinal: Ordinal);

        return true;
    }
}
